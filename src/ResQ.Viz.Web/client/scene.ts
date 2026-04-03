// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

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

    constructor(container: HTMLElement) {
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setSize(window.innerWidth, window.innerHeight);
        this.renderer.shadowMap.enabled = true;
        this.renderer.setClearColor(0x0d1117);
        container.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.scene.fog = new THREE.FogExp2(0x0d1117, 0.0006);

        this._camera = new THREE.PerspectiveCamera(
            55, window.innerWidth / window.innerHeight, 0.1, 5000,
        );
        this._camera.position.set(150, 120, 150);
        this._camera.lookAt(0, 0, 0);

        this._controls = new OrbitControls(this._camera, this.renderer.domElement);
        this._controls.enableDamping  = true;
        this._controls.dampingFactor  = 0.05;
        this._controls.maxPolarAngle  = Math.PI / 2.05;
        this._controls.minDistance    = 5;
        this._controls.maxDistance    = 2000;
        this._controls.target.set(0, 20, 0);

        this._initLights();
        this._initHelpers();
        this._startRenderLoop();
        window.addEventListener('resize', () => this._onResize());
    }

    private _initLights(): void {
        const ambient = new THREE.AmbientLight(0x3a4a5a, 0.8);
        this.scene.add(ambient);

        const sun = new THREE.DirectionalLight(0xfff8e7, 1.4);
        sun.position.set(400, 600, 200);
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        sun.shadow.camera.far = 2000;
        this.scene.add(sun);

        const hemi = new THREE.HemisphereLight(0x224488, 0x1a2e1a, 0.5);
        this.scene.add(hemi);
    }

    private _initHelpers(): void {
        const grid = new THREE.GridHelper(2000, 100, 0x1c2128, 0x161b22);
        grid.position.y = 0.05;
        this.scene.add(grid);
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
                this._camera.position.lerp(desired, 0.06);
                this._controls.target.lerp(targetPos, 0.08);
            }
            this._controls.update();
            this.renderer.render(this.scene, this._camera);
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
