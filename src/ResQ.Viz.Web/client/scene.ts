// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { Sky } from 'three/addons/objects/Sky.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { PostFx } from './postfx';

export class Scene {
    readonly scene: THREE.Scene;
    readonly renderer: THREE.WebGLRenderer;
    private readonly _camera: THREE.PerspectiveCamera;
    private readonly _controls: OrbitControls;
    private _lastTime: number = 0;
    private _frameCount: number = 0;
    private _fps: number = 0;
    private _fpsAccum: number = 0;
    private readonly _tickCallbacks: Array<(dt: number) => void> = [];
    private _followTarget: THREE.Object3D | null = null;
    private readonly _followOffset = new THREE.Vector3(0, 18, -28);
    private _postFx!: PostFx;
    private _sky!: Sky;
    private _freeFly = false;
    private readonly _flyKeys: Record<string, boolean> = {};
    private _flyYaw   = 0;  // radians, horizontal look
    private _flyPitch = 0;  // radians, vertical look, clamped ±80°
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

        this._controls = new OrbitControls(this._camera, this.renderer.domElement);
        this._controls.enableDamping  = true;
        this._controls.dampingFactor  = 0.05;
        this._controls.maxPolarAngle  = Math.PI / 2.05;
        this._controls.minDistance    = 5;    // metres — prevent clipping into drone body
        this._controls.maxDistance    = 2000; // metres — keeps terrain visible at max zoom-out
        this._controls.target.set(0, 20, 0);

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
        this._initFreeFly();
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
            if (this._followTarget) {
                this._controls.enabled = false;
                const targetPos = this._followTarget.position;
                const desired = targetPos.clone().add(this._followOffset);
                const followAlpha = 1 - Math.pow(0.94, dt * 60); // position
                const lookAtAlpha = 1 - Math.pow(0.92, dt * 60); // look-at target
                this._camera.position.lerp(desired, followAlpha);
                this._controls.target.lerp(targetPos, lookAtAlpha);
            }
            if (this._freeFly) {
                // Build look direction from yaw + pitch
                const lookDir = new THREE.Vector3(
                    Math.sin(this._flyYaw) * Math.cos(this._flyPitch),
                    Math.sin(this._flyPitch),
                    Math.cos(this._flyYaw)  * Math.cos(this._flyPitch),
                ).normalize();
                const right = new THREE.Vector3().crossVectors(lookDir, THREE.Object3D.DEFAULT_UP).normalize();

                const speed = (this._flyKeys['ShiftLeft'] || this._flyKeys['ShiftRight']) ? 80 : 20;
                const move  = speed * dt;

                if (this._flyKeys['KeyW'])     this._camera.position.addScaledVector(lookDir,  move);
                if (this._flyKeys['KeyS'])     this._camera.position.addScaledVector(lookDir, -move);
                if (this._flyKeys['KeyA'])     this._camera.position.addScaledVector(right,   -move);
                if (this._flyKeys['KeyD'])     this._camera.position.addScaledVector(right,    move);
                if (this._flyKeys['KeyQ'] || this._flyKeys['Space'])
                                               this._camera.position.y += move;
                if (this._flyKeys['KeyE'])     this._camera.position.y -= move;

                // Apply rotation: look in flyYaw + flyPitch direction
                this._camera.quaternion.setFromEuler(new THREE.Euler(this._flyPitch, this._flyYaw, 0, 'YXZ'));
            }
            this._controls.update();
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
        this._followTarget = obj;
        if (!obj) {
            this._controls.enabled = true;
        }
    }

    get isFollowing(): boolean { return this._followTarget !== null; }

    private _initFreeFly(): void {
        // Pointer lock for mouse look
        document.addEventListener('pointerlockchange', () => {
            this._freeFly = document.pointerLockElement === this.renderer.domElement;
            this._controls.enabled = !this._freeFly;
            if (!this._freeFly) Object.keys(this._flyKeys).forEach(k => { this._flyKeys[k] = false; });
        });

        document.addEventListener('mousemove', (e: MouseEvent) => {
            if (!this._freeFly) return;
            const sens = 0.002;
            this._flyYaw   -= e.movementX * sens;
            this._flyPitch -= e.movementY * sens;
            this._flyPitch  = Math.max(-Math.PI * 0.44, Math.min(Math.PI * 0.44, this._flyPitch));
        });

        document.addEventListener('keydown', (e: KeyboardEvent) => { this._flyKeys[e.code] = true; });
        document.addEventListener('keyup',   (e: KeyboardEvent) => { this._flyKeys[e.code] = false; });
    }

    /** Toggle free-fly camera mode (Pointer Lock + WASD/QE). */
    toggleFreeFly(): void {
        if (this._freeFly) {
            document.exitPointerLock();
        } else {
            // Initialise yaw/pitch from current camera orientation
            const euler = new THREE.Euler().setFromQuaternion(this._camera.quaternion, 'YXZ');
            this._flyYaw   = euler.y;
            this._flyPitch = euler.x;
            this.renderer.domElement.requestPointerLock();
        }
    }

    get isFreeFly(): boolean { return this._freeFly; }

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

    /** Smoothly orbit-target and zoom to frame all given world positions. */
    fitToPositions(positions: THREE.Vector3[]): void {
        if (positions.length === 0) return;
        const box = new THREE.Box3();
        for (const p of positions) box.expandByPoint(p);
        const center = new THREE.Vector3();
        box.getCenter(center);
        const radius = Math.max(box.getSize(new THREE.Vector3()).length() * 0.8, 30);
        this._controls.target.copy(center);
        this._camera.position.set(
            center.x + radius,
            center.y + radius * 0.7,
            center.z + radius,
        );
        this._camera.lookAt(center);
    }
}
