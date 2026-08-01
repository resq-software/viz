// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { Sky } from 'three/addons/objects/Sky.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { PostFx } from './postfx';
import { UnityCamera } from './cameraControl';
import { sunDirection, SUN_COLOR } from './lighting';

// Leading-edge + trailing-edge throttle. `@resq-systems/rate-limiting` offers
// this API but imports `@upstash/ratelimit` at module load for its
// rate-limiter code path (not used here — viz only needs throttle). That
// peer would bundle ~420 KB of unused code or require a resolve-alias
// shim. 15 lines of local throttle is the right trade for one call site.
// `effect@beta` stays installed so future `@resq-systems/*` adoptions that
// don't pull `@upstash/*` can import directly.
function throttle<A extends unknown[]>(fn: (...args: A) => void, waitMs: number): (...args: A) => void {
    let lastCall  = 0;
    let trailing: ReturnType<typeof setTimeout> | null = null;
    let lastArgs: A | null = null;
    return (...args: A) => {
        const now = Date.now();
        lastArgs = args;
        if (now - lastCall >= waitMs) {
            lastCall = now;
            fn(...args);
            return;
        }
        if (!trailing) {
            trailing = setTimeout(() => {
                trailing = null;
                lastCall = Date.now();
                if (lastArgs) fn(...lastArgs);
            }, waitMs - (now - lastCall));
        }
    };
}

export class Scene {
    readonly scene: THREE.Scene;
    readonly renderer: THREE.WebGLRenderer;
    private readonly _camera: THREE.PerspectiveCamera;
    private _cam!: UnityCamera;
    private _lastTime: number = 0;
    private _frameCount: number = 0;
    private _fps: number = 0;
    private _fpsAccum: number = 0;
    private readonly _tickCallbacks: Array<(dt: number) => void> = [];
    // Run after the main composer render each frame — for overlays that draw
    // directly onto the canvas (e.g. the onboard-camera picture-in-picture,
    // which scissor-renders the scene from a second camera into a corner).
    private readonly _postRenderCallbacks: Array<() => void> = [];
    private _postFx!: PostFx;
    private _sky!: Sky;
    private readonly _groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    private _markerMesh: THREE.Mesh | null = null;
    private _markerTimeout: ReturnType<typeof setTimeout> | null = null;

    constructor(container: HTMLElement) {
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setSize(window.innerWidth, window.innerHeight);
        this.renderer.shadowMap.enabled = true;
        this.renderer.shadowMap.type      = THREE.PCFSoftShadowMap;
        this.renderer.toneMapping         = THREE.ACESFilmicToneMapping;
        // Lifted from 1.0: with the flat fill ambient dropped and the env-probe
        // IBL + a stronger directional sun now carrying the scene, a touch more
        // exposure keeps midtones bright without blowing the snow highlights.
        this.renderer.toneMappingExposure = 1.12;
        this.renderer.setClearColor(0x8ab8d4);
        container.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        // Fog colour matches sky horizon so distant terrain dissolves into atmosphere
        // rather than going dark — makes the 4 km terrain feel open.
        this.scene.fog = new THREE.FogExp2(0x8ab8d4, 0.00010);

        this._camera = new THREE.PerspectiveCamera(
            55, window.innerWidth / window.innerHeight, 0.5, 40000,
        );
        this._camera.position.set(150, 120, 150);
        this._camera.lookAt(0, 0, 0);

        this._cam = new UnityCamera(this._camera, this.renderer.domElement);

        this._initSky();
        this._initLights();
        this._initHelpers();
        this._postFx = new PostFx(
            this.renderer,
            this.scene,
            this._camera,
            window.innerWidth,
            window.innerHeight,
        );
        this._startRenderLoop();
        // Resize storms (window drag-resize, devtools docking) can fire
        // dozens of events per second. Throttle to ~10 Hz — the renderer
        // re-layout still feels instant, and we skip ~90 % of the work.
        window.addEventListener('resize', throttle(() => this._onResize(), 100));
    }

    private _initSky(): void {
        const sky = new Sky();
        sky.scale.setScalar(40000);
        this.scene.add(sky);
        this._sky = sky;

        const uniforms = sky.material.uniforms;
        // Bluer, deeper sky than the prior pale (turbidity 4 / rayleigh 0.8)
        // wash. Lower turbidity clears the horizon haze; higher rayleigh
        // deepens the zenith blue so the terrain sits under a real sky, not a
        // flat grey dome. mieG keeps a soft (not pinpoint) sun disc + glow.
        // Bluer, deeper sky than the prior pale wash. NOTE: turbidity above ~4
        // blows the sky out to a milky white-out that the pale terrain washes
        // into — keep it low. Atmospheric depth comes from fog + cloud shadows,
        // not from cranking sky haze.
        uniforms['turbidity']!.value          = 3.2;
        uniforms['rayleigh']!.value           = 1.6;
        uniforms['mieCoefficient']!.value     = 0.005;
        uniforms['mieDirectionalG']!.value    = 0.86;

        // One canonical sun direction, shared with the DirectionalLight and the
        // Water specular via ./lighting — see that module for why this used to
        // be three disagreeing vectors.
        const sun = sunDirection();
        uniforms['sunPosition']!.value.copy(sun);

        // Image-based lighting probe. Rendered from RoomEnvironment (a neutral
        // studio box), NOT from the Sky mesh: running the PMREM pass over the
        // procedural Sky shader corrupts GL state on software-GL stacks and
        // leaves the live sky rendering solid black. RoomEnvironment gives every
        // PBR surface (terrain, rock, buildings, drones, water) soft specular
        // fill and works everywhere. The sky's *colour* still reaches the scene
        // through the retuned hemisphere + ambient fill in _initLights.
        const pmrem = new THREE.PMREMGenerator(this.renderer);
        const envRT = pmrem.fromScene(new RoomEnvironment());
        this.scene.environment = envRT.texture;
        // Scale the probe's contribution so it fills shadows without flattening
        // the directional sun's contrast (default 1.0 washed everything out).
        this.scene.environmentIntensity = 0.55;
        pmrem.dispose();

        // Sky mesh handles background — ensure no solid color overwrites it
        this.scene.background = null;
    }

    private _initLights(): void {
        // FILL now comes mostly from the environment IBL probe
        // (scene.environment) set in _initSky, so the heavy flat fill ambient —
        // the main cause of the washed-out, contrast-free look — is cut right
        // back. A whisker of cool ambient just keeps deep shadows off pure
        // black; the env probe + hemisphere do the real shadow fill.
        const ambient = new THREE.AmbientLight(0x5b6a7a, 0.22);
        this.scene.add(ambient);

        // Directional sun aligned to the visible Sky disc and Water specular.
        // Intensity lifted 1.8 → 2.6 to carry the scene now that flat ambient
        // no longer floods every surface — this is what puts the light/shadow
        // contrast back into the terrain relief.
        const sun = new THREE.DirectionalLight(SUN_COLOR, 2.6);
        sun.position.copy(sunDirection()).multiplyScalar(1500);
        sun.castShadow = true;
        // 4096 × 4096 shadow map with a ±800 m frustum. Net near-camera texel
        // density is ~3.4× sharper than the prior 2048² / ±1200 m setup
        // (0.39 m/texel vs 1.17 m/texel). Drone shadows at mid-camera range
        // gain the crispness CSM would deliver without any shader-chunk
        // mutation or material-registration plumbing. 64 MB shadow RAM on
        // modern GPUs is trivial; the ±800 m frustum still covers the
        // active drone area in every shipped scenario — the spawn radii
        // top out at ~520 m in multi-agency-sar.
        sun.shadow.mapSize.set(4096, 4096);
        sun.shadow.camera.near   =   10;
        sun.shadow.camera.far    = 4000;
        sun.shadow.camera.left   = -800;
        sun.shadow.camera.right  =  800;
        sun.shadow.camera.top    =  800;
        sun.shadow.camera.bottom = -800;
        // Tighter frustum halves shadow-acne amplitude; bias can relax
        // from -0.0018 to -0.0010 without re-introducing the edge artifact.
        sun.shadow.bias          = -0.0010;
        sun.shadow.normalBias    =  0.02;     // reduce peter-panning on trees
        // After mutating the orthographic frustum bounds the projection
        // matrix must be recomputed — otherwise Three.js renders the shadow
        // map using the default ±5 bounds and drones outside that tiny
        // footprint cast no shadow at all.
        sun.shadow.camera.updateProjectionMatrix();
        this.scene.add(sun);

        // Sky/ground hemisphere adds a directional tint to the fill the IBL
        // probe can't (cool sky-blue from above, warm earth bounce from below).
        // Trimmed well down so it complements rather than competes with the
        // env probe.
        const hemi = new THREE.HemisphereLight(0x8fb2d8, 0x4a4030, 0.45);
        this.scene.add(hemi);
    }

    private _initHelpers(): void {
        // GridHelper removed — caused Z-fighting with displaced terrain vertices
    }

    private _startRenderLoop(): void {
        this._lastTime = performance.now();

        const loop = (now: number): void => {
            requestAnimationFrame(loop);
            const dt = Math.min((now - this._lastTime) / 1000, 0.1); // cap at 100 ms
            this._lastTime = now;
            this._fpsAccum += dt;
            this._frameCount++;
            if (this._frameCount % 30 === 0) {
                this._fps = Math.round(30 / this._fpsAccum); // avg over 30-frame window
                this._fpsAccum = 0;
            }
            for (const cb of this._tickCallbacks) cb(dt);
            this._cam.update(dt);
            this._postFx.render();
            for (const cb of this._postRenderCallbacks) cb();
        };
        requestAnimationFrame(loop);
    }

    addTickCallback(fn: (dt: number) => void): void {
        this._tickCallbacks.push(fn);
    }

    /**
     * Register a callback that runs after the main composer render each frame.
     * Use for canvas overlays drawn with the renderer directly (scissor views);
     * such a callback must restore renderer viewport/scissor state before
     * returning so the next frame's composer renders full-screen.
     */
    addPostRenderCallback(fn: () => void): void {
        this._postRenderCallbacks.push(fn);
    }

    getIntersections(clientX: number, clientY: number, objects: THREE.Object3D[]): THREE.Intersection[] {
        if (objects.length === 0) return [];
        const rect = this.renderer.domElement.getBoundingClientRect();
        const ndc = new THREE.Vector2(
            ((clientX - rect.left)  / rect.width)  * 2 - 1,
            -((clientY - rect.top)  / rect.height) * 2 + 1,
        );
        const raycaster = new THREE.Raycaster();
        raycaster.setFromCamera(ndc, this._camera);
        return raycaster.intersectObjects(objects, true);
    }

    private _onResize(): void {
        this._camera.aspect = window.innerWidth / window.innerHeight;
        this._camera.updateProjectionMatrix();
        this.renderer.setSize(window.innerWidth, window.innerHeight);
        this._postFx.setSize(window.innerWidth, window.innerHeight);
    }

    get fps(): number { return this._fps; }

    /** Attach camera follow to a scene object (pass null to release). */
    followObject(obj: THREE.Object3D | null): void {
        this._cam.followObject(obj);
    }

    /** Chase-follow an object (camera behind its heading, looking forward). Pass null to release. */
    chaseObject(obj: THREE.Object3D | null): void {
        this._cam.chaseObject(obj);
    }

    /** FPV / onboard view: ride at the object's nose looking forward. Pass null to release. */
    fpvObject(obj: THREE.Object3D | null): void {
        this._cam.fpvObject(obj);
    }

    /** Set the camera's vertical field of view (deg) — FPV widens it for an immersive feed. */
    setCameraFov(deg: number): void {
        this._cam.setFov(deg);
    }

    get isFollowing(): boolean { return this._cam.isFollowing; }
    get isFlying(): boolean    { return this._cam.isFlying; }

    /** Underlying camera controller — exposed for scripted-playback clients. */
    get cameraController(): UnityCamera { return this._cam; }

    /**
     * Camera state projected to the XZ plane (world-space) plus the current
     * field-of-view in degrees. Consumed by the mini-map to render a
     * viewport-frustum indicator so the operator sees what's in view.
     */
    getCameraState(): { x: number; z: number; fwd: { x: number; z: number }; fov: number } {
        const dir = new THREE.Vector3();
        this._camera.getWorldDirection(dir);
        const fwdLen = Math.hypot(dir.x, dir.z) || 1;
        return {
            x: this._camera.position.x,
            z: this._camera.position.z,
            fwd: { x: dir.x / fwdLen, z: dir.z / fwdLen },
            fov: this._camera.fov,
        };
    }

    /** Smoothly orbit-target and zoom to frame all given world positions. */
    fitToPositions(positions: THREE.Vector3[]): void {
        this._cam.fitToPositions(positions);
    }

    get flySpeed(): number { return this._cam.flySpeed; }
    set flySpeed(v: number) { this._cam.flySpeed = v; }

    setBloomEnabled(v: boolean): void  { this._postFx.setBloomEnabled(v); }
    setBloomStrength(v: number): void  { this._postFx.setBloomStrength(v); }
    setFogDensity(v: number): void {
        if (this.scene.fog instanceof THREE.FogExp2) this.scene.fog.density = v;
    }
    setAtmosphere(fogColor: number, density: number): void {
        if (this.scene.fog instanceof THREE.FogExp2) {
            this.scene.fog.color.set(fogColor);
            this.scene.fog.density = density;
        }
        this.renderer.setClearColor(fogColor);
    }
    setFov(degrees: number): void {
        this._camera.fov = degrees;
        this._camera.updateProjectionMatrix();
    }
    setShadowsEnabled(v: boolean): void {
        this.renderer.shadowMap.enabled = v;
        // Force shadow map refresh
        this.scene.traverse(obj => {
            const m = obj as THREE.Mesh;
            if (m.isMesh) m.castShadow = m.castShadow; // touch to trigger refresh
        });
    }

    getTerrainIntersection(clientX: number, clientY: number): THREE.Vector3 | null {
        const rect   = this.renderer.domElement.getBoundingClientRect();
        const ndc    = new THREE.Vector2(
            ((clientX - rect.left) / rect.width)  * 2 - 1,
            -((clientY - rect.top) / rect.height) * 2 + 1,
        );
        const ray = new THREE.Raycaster();
        ray.setFromCamera(ndc, this._camera);
        const target = new THREE.Vector3();
        const hit    = ray.ray.intersectPlane(this._groundPlane, target);
        return hit ? target : null;
    }

    showTargetMarker(pos: THREE.Vector3, alt: number): void {
        void alt; // alt unused here — marker is always on ground plane
        if (!this._markerMesh) {
            const geo = new THREE.RingGeometry(1.5, 2.5, 32);
            geo.rotateX(-Math.PI / 2);
            const mat = new THREE.MeshBasicMaterial({
                color: 0x21D4FD, transparent: true, opacity: 0.8, side: THREE.DoubleSide,
            });
            this._markerMesh = new THREE.Mesh(geo, mat);
            this.scene.add(this._markerMesh);
        }
        this._markerMesh.position.set(pos.x, 0.1, pos.z);
        this._markerMesh.visible = true;
        (this._markerMesh.material as THREE.MeshBasicMaterial).opacity = 0.8;

        if (this._markerTimeout) clearTimeout(this._markerTimeout);
        this._markerTimeout = setTimeout(() => {
            if (this._markerMesh) this._markerMesh.visible = false;
        }, 2000);
    }
}
