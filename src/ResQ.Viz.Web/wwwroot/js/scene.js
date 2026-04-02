// ResQ Viz - Three.js scene setup
// SPDX-License-Identifier: Apache-2.0

export class Scene {
    constructor(container) {
        this.container = container;
        this._initRenderer();
        this._initCamera();
        this._initLights();
        this._initHelpers();
        this._startRenderLoop();
        window.addEventListener('resize', () => this._onResize());
    }

    _initRenderer() {
        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.setSize(window.innerWidth, window.innerHeight);
        this.renderer.shadowMap.enabled = true;
        this.renderer.setClearColor(0x0d1117);
        this.container.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.scene.fog = new THREE.Fog(0x0d1117, 800, 2000);
    }

    _initCamera() {
        this.camera = new THREE.PerspectiveCamera(60, window.innerWidth / window.innerHeight, 0.1, 5000);
        this.camera.position.set(200, 200, 200);
        this.camera.lookAt(0, 0, 0);

        this.controls = new THREE.OrbitControls(this.camera, this.renderer.domElement);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.05;
        this.controls.maxPolarAngle = Math.PI / 2.1;
        this.controls.minDistance = 10;
        this.controls.maxDistance = 2000;
    }

    _initLights() {
        // Ambient
        const ambient = new THREE.AmbientLight(0x404060, 0.6);
        this.scene.add(ambient);

        // Directional (sun)
        const sun = new THREE.DirectionalLight(0xfff8e7, 1.2);
        sun.position.set(300, 500, 200);
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        this.scene.add(sun);

        // Sky hemisphere
        const hemi = new THREE.HemisphereLight(0x87ceeb, 0x3d5a3e, 0.4);
        this.scene.add(hemi);
    }

    _initHelpers() {
        const grid = new THREE.GridHelper(1000, 50, 0x1c2128, 0x1c2128);
        this.scene.add(grid);
    }

    _startRenderLoop() {
        this._lastTime = performance.now();
        this._frameCount = 0;
        this._fps = 0;
        this._tickCallbacks = [];

        const loop = (now) => {
            requestAnimationFrame(loop);
            const dt = (now - this._lastTime) / 1000;
            this._lastTime = now;
            this._frameCount++;
            if (this._frameCount % 30 === 0) {
                this._fps = Math.round(1 / dt);
            }
            for (const cb of this._tickCallbacks) cb(dt);
            this.controls.update();
            this.renderer.render(this.scene, this.camera);
        };
        requestAnimationFrame(loop);
    }

    /**
     * Register a callback to be invoked each render frame.
     * @param {Function} fn - Callback with no arguments
     */
    addTickCallback(fn) { this._tickCallbacks.push(fn); }

    _onResize() {
        this.camera.aspect = window.innerWidth / window.innerHeight;
        this.camera.updateProjectionMatrix();
        this.renderer.setSize(window.innerWidth, window.innerHeight);
    }

    get fps() { return this._fps; }
}
