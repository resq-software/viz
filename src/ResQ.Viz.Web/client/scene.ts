// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { Sky } from 'three/addons/objects/Sky.js';
import { PostFx } from './postfx';
import { UnityCamera } from './cameraControl';
import { updateWaterSunDirection } from './water';
import { getLogger } from './log';
import {
    DEFAULT_SUN_AZIMUTH_DEG,
    DEFAULT_SUN_ELEVATION_DEG,
    shadowBiasFor,
    shadowDepthRange,
    shadowExtentFor,
    snapToShadowTexel,
    sunDirection,
    sunDistance,
    viewGroundFootprint,
} from './lighting';

/**
 * Tallest shadow caster in the world, metres. Measured — not guessed — by the
 * `caster envelope` test in `__tests__/lighting.test.ts`, which sweeps every
 * preset's height function on a 10 m grid. Current maxima: ridgeline 235.7,
 * alpine 132.2, canyon 106.3, coastal 49.4, dunes 43.3. Trees do not raise it
 * (each preset's `maxTreeH` is a *planting altitude ceiling* well below its
 * peak, so summits are bare). The headroom above 235.7 covers structures.
 *
 * Raising a preset's terrain past this sinks the directional light into the
 * terrain at low sun elevation — re-run that test and bump this if you do.
 */
const CASTER_ENVELOPE_M = 260;

/**
 * Distance cap, metres, for the view-frustum ground projection the shadow
 * frustum is fitted to. Beyond this, cast shadow is below the perceptual
 * threshold anyway and widening only costs texel density.
 */
const MAX_SHADOW_DISTANCE_M = 3400;

/** Shadow map resolution per axis. */
const SHADOW_MAP_SIZE = 4096;

/**
 * Base intensity of the directional sun at `sunIntensity = 1.0`. Atmospheric
 * classes scale this — see {@link Scene.setSkyProfile}.
 */
const BASE_SUN_INTENSITY = 1.8;

const log = getLogger('scene');

/**
 * The Scene-owned subset of an environment descriptor.
 *
 * `ScenarioEnvironment` in ./scenarioEnvironments extends this with the facets
 * Scene does not own (terrain preset, water level, camera framing), so the two
 * layers share one vocabulary without Scene importing the scenario table.
 */
export interface SceneEnvironment {
    /** Sun elevation above the horizon, degrees. */
    readonly sunElevationDeg: number;
    /** Sun compass azimuth, degrees (three.js convention: 0 → +Z, 90 → +X). */
    readonly sunAzimuthDeg: number;
    /** Fog colour; also the renderer clear colour. */
    readonly fogColor: number;
    /** Base extinction, `FogExp2.density` units. */
    readonly fogDensity: number;
    /**
     * Vertical fog falloff, 1/metres. Carried for contract stability but
     * currently INERT — height fog is written (`./heightFog`) but unwired
     * pending the black-terrain root cause. Setting this has no effect yet.
     */
    readonly fogHeightFalloff?: number;
    /** ACES exposure. Below 1.0 for high-albedo scenes so snow keeps relief. */
    readonly toneMappingExposure: number;
}

// Leading-edge + trailing-edge throttle. `@resq-sw/rate-limiting` offers
// this API but imports `@upstash/ratelimit` at module load for its
// rate-limiter code path (not used here — viz only needs throttle). That
// peer would bundle ~420 KB of unused code or require a resolve-alias
// shim. 15 lines of local throttle is the right trade for one call site.
// `effect@beta` stays installed so future `@resq-sw/*` adoptions that
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
    private _postFx!: PostFx;
    private _sky!: Sky;
    private _sun!: THREE.DirectionalLight;
    private _pmrem!: THREE.PMREMGenerator;
    private _envRT: THREE.WebGLRenderTarget | null = null;
    // Single source of truth for the sun. Sky, directional light, water glint,
    // and the PBR environment map are all derived from this so the visible
    // sun, the cast shadows, and the surface lighting stay in agreement.
    private readonly _sunDir = new THREE.Vector3();
    private _sunElevDeg    = DEFAULT_SUN_ELEVATION_DEG;
    private _sunAzimuthDeg = DEFAULT_SUN_AZIMUTH_DEG;
    // Current shadow-frustum rung. Tracked so bias/extent are only rewritten on
    // a ladder step, not every frame — see `_updateShadowFrustum`.
    private _shadowExtent = 0;
    private readonly _shadowCenter = new THREE.Vector3();
    private readonly _groundPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    private _markerMesh: THREE.Mesh | null = null;
    private _markerTimeout: ReturnType<typeof setTimeout> | null = null;

    constructor(container: HTMLElement) {
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setSize(window.innerWidth, window.innerHeight);
        this.renderer.shadowMap.enabled = true;
        this.renderer.shadowMap.type      = THREE.PCFShadowMap;
        this.renderer.toneMapping         = THREE.ACESFilmicToneMapping;
        this.renderer.toneMappingExposure = 1.0;
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

        this._computeSunDir();
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

    private _computeSunDir(): void {
        // Spherical → cartesian using the Sky addon's convention. The maths
        // lives in ./lighting so it is unit-testable without a WebGL context;
        // one computation feeds every sun-dependent system. See
        // {@link setSunPosition}.
        sunDirection(this._sunElevDeg, this._sunAzimuthDeg, this._sunDir);
    }

    /**
     * Place the directional light along the sun vector far enough out to clear
     * the tallest caster.
     *
     * A fixed 1500 m fails at low sun: 1500·sin(6°) ≈ 157 m puts the light
     * *below* ridgeline's 235.7 m peaks, so the terrain that should cast is
     * behind the light and the shadow set is wrong. Distance scales as
     * 1/sin(elevation) — see {@link sunDistance}.
     */
    private _positionSun(): void {
        const d = sunDistance(this._sunElevDeg, CASTER_ENVELOPE_M);
        this._sun.position.copy(this._sunDir).multiplyScalar(d);
        this._sun.target.position.copy(this._shadowCenter);
        this._sun.target.updateMatrixWorld();
        updateWaterSunDirection(this._sun.position);
    }

    /**
     * Resize the ortho shadow frustum to a ladder rung and rescale depth range
     * and bias to match. Only called on a rung change, never per-frame.
     */
    private _applyShadowExtent(extent: number): void {
        this._shadowExtent = extent;
        const cam = this._sun.shadow.camera;
        cam.left   = -extent;
        cam.right  =  extent;
        cam.top    =  extent;
        cam.bottom = -extent;

        // far=4000 is only correct while distance is pinned at 1500. Once
        // distance scales with elevation, a fixed far silently clips the far
        // half of the caster set at exactly the low sun angles that make relief
        // legible.
        const d = sunDistance(this._sunElevDeg, CASTER_ENVELOPE_M);
        const { near, far } = shadowDepthRange(d, extent, CASTER_ENVELOPE_M);
        cam.near = near;
        cam.far  = far;

        // Acne amplitude tracks texel world size, so bias tuned at ±800 m
        // under-corrects by 4× at the ±3200 m rung.
        const { bias, normalBias } = shadowBiasFor(extent, SHADOW_MAP_SIZE);
        this._sun.shadow.bias       = bias;
        this._sun.shadow.normalBias = normalBias;

        // Mutating ortho bounds without this leaves Three.js rendering the
        // shadow map at the default ±5 bounds — nothing outside that tiny
        // footprint casts at all.
        cam.updateProjectionMatrix();
    }

    /**
     * Refit the shadow frustum to the current view, once per frame.
     *
     * Fitted to the view frustum's ground projection rather than to the orbit
     * target: in free-fly the target sits behind the camera when you look at a
     * distant ridge, so a target-fitted frustum excludes the geometry that most
     * needs to cast. The centre is snapped to the shadow-texel grid to stop
     * texels crawling over static terrain, which is only meaningful because the
     * extent is quantised to rungs — a continuously-varying extent changes texel
     * size every frame and makes snapping a no-op.
     */
    private _updateShadowFrustum(): void {
        if (!this.renderer.shadowMap.enabled) return;

        const { center, radius } = viewGroundFootprint(this._camera, MAX_SHADOW_DISTANCE_M);
        const extent = shadowExtentFor(radius);
        if (extent !== this._shadowExtent) this._applyShadowExtent(extent);

        snapToShadowTexel(center, extent, SHADOW_MAP_SIZE, this._sunDir, this._shadowCenter);
        this._sun.position.copy(this._shadowCenter)
            .addScaledVector(this._sunDir, sunDistance(this._sunElevDeg, CASTER_ENVELOPE_M));
        this._sun.target.position.copy(this._shadowCenter);
        this._sun.target.updateMatrixWorld();
    }

    private _initSky(): void {
        const sky = new Sky();
        sky.scale.setScalar(40000);
        this.scene.add(sky);
        this._sky = sky;

        const uniforms = sky.material.uniforms;
        uniforms['turbidity']!.value          = 4;
        uniforms['rayleigh']!.value           = 0.8;
        uniforms['mieCoefficient']!.value     = 0.005;
        uniforms['mieDirectionalG']!.value    = 0.92;
        uniforms['sunPosition']!.value.copy(this._sunDir);

        // Environment map is baked FROM the Sky (not RoomEnvironment) so PBR
        // reflections pick up the real sky colour and sun position — the
        // RoomEnvironment had no relationship to the visible sky.
        this._pmrem = new THREE.PMREMGenerator(this.renderer);
        this._pmrem.compileEquirectangularShader();
        this._bakeEnvFromSky();

        // Sky mesh handles background — ensure no solid color overwrites it
        this.scene.background = null;
    }

    /**
     * Re-bake the PBR environment map from the current Sky state. The Sky mesh
     * is temporarily reparented into a throwaway scene because
     * `PMREMGenerator.fromScene` captures a whole scene — we want only the
     * atmosphere reflected in surfaces, not terrain/drones/helpers.
     */
    private _bakeEnvFromSky(): void {
        if (this._envRT) this._envRT.dispose();
        const envScene = new THREE.Scene();
        envScene.add(this._sky);            // detaches from the main scene
        // Pass explicit far=50000 — the Sky mesh is scaled 40000 and the
        // default fromScene far plane is 100, which clipped the entire dome
        // and made the baked env map black. (sigma=0 keeps default blur.)
        this._envRT = this._pmrem.fromScene(envScene, 0, 0.1, 50000);
        this.scene.add(this._sky);          // re-attach to the main scene
        this.scene.environment = this._envRT.texture;
        this._warnIfEnvBlack();
    }

    /**
     * Fail loudly when the PBR environment probe bakes to black.
     *
     * A black env map is indistinguishable by eye from a lighting regression —
     * every PBR surface just goes flat and dark — so it must be detected, not
     * observed. The usual cause is `PMREMGenerator` defaulting to
     * `HalfFloatType` on stacks whose `OES_texture_half_float_linear` support is
     * unreliable (SwiftShader, i.e. every headless screenshot). If this fires,
     * force `FloatType` or gate the bake behind a flag — do not "fix" the
     * lighting.
     */
    private _warnIfEnvBlack(): void {
        const rt = this._envRT;
        if (!rt) return;
        try {
            const w = Math.min(8, rt.width);
            const h = Math.min(8, rt.height);
            const n = w * h * 4;
            const type = rt.texture.type;
            const buf =
                type === THREE.UnsignedByteType ? new Uint8Array(n)   :
                type === THREE.FloatType        ? new Float32Array(n) :
                                                  new Uint16Array(n);
            this.renderer.readRenderTargetPixels(rt, 0, 0, w, h, buf);
            // Half-float 0.0 is all-zero bits, so a plain truthiness scan is a
            // valid nonzero-luminance test for every buffer type above.
            for (let i = 0; i < n; i += 4) {
                if (buf[i] || buf[i + 1] || buf[i + 2]) return;
            }
            log.warn(
                'environment probe baked black — PBR surfaces will render unlit. ' +
                'Likely half-float render-target support, not a lighting bug.',
                { textureType: type, renderer: this.renderer.getContext().getParameter(0x1F01) },
            );
        } catch (err) {
            log.debug('env probe readback unavailable', { err });
        }
    }

    private _initLights(): void {
        const ambient = new THREE.AmbientLight(0x3a4a5a, 0.8);
        this.scene.add(ambient);

        const sun = new THREE.DirectionalLight(0xfff8e7, BASE_SUN_INTENSITY);
        this._sun = sun;
        this._positionSun();
        sun.castShadow = true;
        sun.shadow.mapSize.set(SHADOW_MAP_SIZE, SHADOW_MAP_SIZE);
        // The frustum is not fixed. A ±800 m box on a 4000 m world leaves ~84 %
        // of the map with no cast shadow at all, which is why relief read flat
        // at overview distance regardless of heightfield quality. `_updateShadow
        // Frustum` refits it to the view every frame; this just seeds a rung so
        // the first frame before any camera update is already valid.
        this._applyShadowExtent(shadowExtentFor(0));
        // The shadow camera is a child of the light, so its target must be in
        // the scene graph for the light's matrix to resolve.
        this.scene.add(sun);
        this.scene.add(sun.target);

        const hemi = new THREE.HemisphereLight(0x224488, 0x1a2e1a, 0.5);
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
            // After the camera moves, before anything renders — the shadow
            // frustum follows the view.
            this._updateShadowFrustum();
            this._postFx.render();
        };
        requestAnimationFrame(loop);
    }

    addTickCallback(fn: (dt: number) => void): void {
        this._tickCallbacks.push(fn);
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
    setSsaoEnabled(v: boolean): void   { this._postFx.setSsaoEnabled(v); }
    setSsaoIntensity(v: number): void  { this._postFx.setSsaoIntensity(v); }
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

    /**
     * Reposition the sun (degrees: elevation above horizon, azimuth around Y).
     * Updates the Sky, the directional light, the water glint, and re-bakes the
     * environment map in one shot so every lighting cue stays coherent.
     */
    setSunPosition(elevationDeg: number, azimuthDeg: number): void {
        this._sunElevDeg    = elevationDeg;
        this._sunAzimuthDeg = azimuthDeg;
        this._computeSunDir();
        this._sky.material.uniforms['sunPosition']!.value.copy(this._sunDir);
        this._positionSun();
        // Light distance and therefore the depth range both moved; re-derive the
        // frustum at the current rung rather than only updating the projection.
        this._applyShadowExtent(this._shadowExtent || shadowExtentFor(0));
        this._bakeEnvFromSky();
    }

    /**
     * Scene exposure. Per-environment because ACES flattens high-albedo scenes:
     * snow blows out to featureless white at 1.0, destroying exactly the relief
     * an alpine scenario exists to show.
     */
    setToneMappingExposure(v: number): void {
        this.renderer.toneMappingExposure = v;
    }

    /**
     * Apply every Scene-owned facet of an environment in one call.
     *
     * This is the INNER seam. It owns only what `Scene` owns — sun, fog,
     * exposure — and deliberately knows nothing about terrain presets, water
     * level, or camera framing. The OUTER orchestrator,
     * `applyScenarioEnvironment` in ./scenarioEnvironments, wraps this and adds
     * those layers. The two have different names on purpose: an earlier plan
     * had both called `applyEnvironment`, which would have let a Scene-level
     * and a scenario-level function drift apart under one identifier.
     *
     * Ordering matters. Sun goes first because `setSunPosition` re-bakes the
     * environment probe, and the fog colour is chosen to match the sky horizon
     * that bake produces.
     */
    applyEnvironment(env: SceneEnvironment): void {
        this.setSunPosition(env.sunElevationDeg, env.sunAzimuthDeg);
        this.setAtmosphere(env.fogColor, env.fogDensity);
        this.setToneMappingExposure(env.toneMappingExposure);
    }

    /**
     * Atmospheric class: Sky shader parameters plus a direct-sun multiplier.
     *
     * `mieG` is the load-bearing one at low sun. The Preetham sky keeps a hard
     * sun disc at any turbidity, so a 6° hurricane reads as a sunset unless the
     * aureole is spread wide — and "looks like a sunset" is a scenario-
     * distinctness failure, not a cosmetic one.
     *
     * Re-bakes the environment probe because the sky it is derived from changed.
     */
    setSkyProfile(p: {
        turbidity: number; rayleigh: number; mieG: number; sunIntensity: number;
    }): void {
        const u = this._sky.material.uniforms;
        u['turbidity']!.value       = p.turbidity;
        u['rayleigh']!.value        = p.rayleigh;
        u['mieDirectionalG']!.value = p.mieG;
        this._sun.intensity = BASE_SUN_INTENSITY * p.sunIntensity;
        this._bakeEnvFromSky();
    }

    getTerrainIntersection(clientX: number, clientY: number, groundMesh?: THREE.Mesh | null): THREE.Vector3 | null {
        const rect   = this.renderer.domElement.getBoundingClientRect();
        const ndc    = new THREE.Vector2(
            ((clientX - rect.left) / rect.width)  * 2 - 1,
            -((clientY - rect.top) / rect.height) * 2 + 1,
        );
        const ray = new THREE.Raycaster();
        ray.setFromCamera(ndc, this._camera);

        if (groundMesh) {
            const hits = ray.intersectObject(groundMesh, false);
            if (hits.length > 0 && hits[0]!.point) {
                return hits[0]!.point;
            }
        }

        const target = new THREE.Vector3();
        const hit    = ray.ray.intersectPlane(this._groundPlane, target);
        return hit ? target : null;
    }

    showTargetMarker(pos: THREE.Vector3, alt: number): void {
        void alt;
        if (!this._markerMesh) {
            const geo = new THREE.RingGeometry(1.5, 2.5, 32);
            geo.rotateX(-Math.PI / 2);
            const mat = new THREE.MeshBasicMaterial({
                color: 0x21D4FD, transparent: true, opacity: 0.8, side: THREE.DoubleSide,
            });
            this._markerMesh = new THREE.Mesh(geo, mat);
            this.scene.add(this._markerMesh);
        }
        this._markerMesh.position.set(pos.x, pos.y + 0.2, pos.z);
        this._markerMesh.visible = true;
        (this._markerMesh.material as THREE.MeshBasicMaterial).opacity = 0.8;

        if (this._markerTimeout) clearTimeout(this._markerTimeout);
        this._markerTimeout = setTimeout(() => {
            if (this._markerMesh) this._markerMesh.visible = false;
        }, 2000);
    }
}
