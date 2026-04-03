// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { Sky } from 'three/addons/objects/Sky.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { PostFx } from './postfx';
import { UnityCamera } from './cameraControl';

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
        this.renderer.toneMappingExposure = 1.0;
        this.renderer.setClearColor(0x0d1117);
        container.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        // Fog density: tuned for 600-unit terrain; increase for denser atmosphere
        this.scene.fog = new THREE.FogExp2(0x0d1117, 0.00015);

        this._camera = new THREE.PerspectiveCamera(
            55, window.innerWidth / window.innerHeight, 0.1, 20000,
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
        window.addEventListener('resize', () => this._onResize());
    }

    private _initSky(): void {
        const sky = new Sky();
        sky.scale.setScalar(10000);
        this.scene.add(sky);
        this._sky = sky;

        const sun = new THREE.Vector3();
        const uniforms = sky.material.uniforms;
        uniforms['turbidity']!.value          = 4;
        uniforms['rayleigh']!.value           = 0.8;
        uniforms['mieCoefficient']!.value     = 0.005;
        uniforms['mieDirectionalG']!.value    = 0.92;

        // Elevation ~30° → morning light angle
        const phi   = THREE.MathUtils.degToRad(90 - 30);
        const theta = THREE.MathUtils.degToRad(180);
        sun.setFromSphericalCoords(1, phi, theta);
        uniforms['sunPosition']!.value.copy(sun);

        // Sync directional sun light direction to match sky
        this.scene.traverse(obj => {
            if (obj instanceof THREE.DirectionalLight && obj.castShadow) {
                obj.position.set(
                    sun.x * 500,
                    sun.y * 500,
                    sun.z * 500,
                );
            }
        });

        // Generate environment map from sky for PBR reflections
        const pmrem = new THREE.PMREMGenerator(this.renderer);
        pmrem.compileEquirectangularShader();
        const envMap = pmrem.fromScene(new RoomEnvironment()).texture;
        this.scene.environment = envMap;
        pmrem.dispose();

        // Sky mesh handles background — ensure no solid color overwrites it
        this.scene.background = null;
    }

    private _initLights(): void {
        const ambient = new THREE.AmbientLight(0x3a4a5a, 0.8);
        this.scene.add(ambient);

        const sun = new THREE.DirectionalLight(0xfff8e7, 1.8);
        sun.position.set(400, 600, 200);
        sun.castShadow = true;
        sun.shadow.mapSize.set(4096, 4096);
        sun.shadow.camera.near   =   10;
        sun.shadow.camera.far    = 2000;
        sun.shadow.camera.left   = -600;
        sun.shadow.camera.right  =  600;
        sun.shadow.camera.top    =  600;
        sun.shadow.camera.bottom = -600;
        sun.shadow.bias          = -0.002;
        this.scene.add(sun);

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
