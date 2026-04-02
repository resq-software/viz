// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

export class Scene {
    readonly scene: THREE.Scene;
    private readonly _renderer: THREE.WebGLRenderer;
    private readonly _camera: THREE.PerspectiveCamera;
    private readonly _controls: OrbitControls;
    private _lastTime: number = 0;
    private _frameCount: number = 0;
    private _fps: number = 0;
    private readonly _tickCallbacks: Array<(dt: number) => void> = [];

    constructor(container: HTMLElement) {
        this._renderer = new THREE.WebGLRenderer({ antialias: true });
        this._renderer.setPixelRatio(window.devicePixelRatio);
        this._renderer.setSize(window.innerWidth, window.innerHeight);
        this._renderer.shadowMap.enabled = true;
        this._renderer.setClearColor(0x0d1117);
        container.appendChild(this._renderer.domElement);

        this.scene = new THREE.Scene();
        this.scene.fog = new THREE.Fog(0x0d1117, 800, 2000);

        this._camera = new THREE.PerspectiveCamera(
            60, window.innerWidth / window.innerHeight, 0.1, 5000,
        );
        this._camera.position.set(200, 200, 200);
        this._camera.lookAt(0, 0, 0);

        this._controls = new OrbitControls(this._camera, this._renderer.domElement);
        this._controls.enableDamping = true;
        this._controls.dampingFactor = 0.05;
        this._controls.maxPolarAngle = Math.PI / 2.1;
        this._controls.minDistance = 10;
        this._controls.maxDistance = 2000;

        this._initLights();
        this._initHelpers();
        this._startRenderLoop();
        window.addEventListener('resize', () => this._onResize());
    }

    private _initLights(): void {
        const ambient = new THREE.AmbientLight(0x404060, 0.6);
        this.scene.add(ambient);

        const sun = new THREE.DirectionalLight(0xfff8e7, 1.2);
        sun.position.set(300, 500, 200);
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        this.scene.add(sun);

        const hemi = new THREE.HemisphereLight(0x87ceeb, 0x3d5a3e, 0.4);
        this.scene.add(hemi);
    }

    private _initHelpers(): void {
        const grid = new THREE.GridHelper(1000, 50, 0x1c2128, 0x1c2128);
        this.scene.add(grid);
    }

    private _startRenderLoop(): void {
        this._lastTime = performance.now();

        const loop = (now: number): void => {
            requestAnimationFrame(loop);
            const dt = (now - this._lastTime) / 1000;
            this._lastTime = now;
            this._frameCount++;
            if (this._frameCount % 30 === 0) {
                this._fps = Math.round(1 / dt);
            }
            for (const cb of this._tickCallbacks) cb(dt);
            this._controls.update();
            this._renderer.render(this.scene, this._camera);
        };
        requestAnimationFrame(loop);
    }

    addTickCallback(fn: (dt: number) => void): void {
        this._tickCallbacks.push(fn);
    }

    private _onResize(): void {
        this._camera.aspect = window.innerWidth / window.innerHeight;
        this._camera.updateProjectionMatrix();
        this._renderer.setSize(window.innerWidth, window.innerHeight);
    }

    get fps(): number { return this._fps; }
}
