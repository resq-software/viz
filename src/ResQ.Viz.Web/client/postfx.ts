// ResQ Viz - Post-processing effects pipeline (selective bloom)
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { EffectComposer }  from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass }      from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { ShaderPass }      from 'three/addons/postprocessing/ShaderPass.js';
import { OutputPass }      from 'three/addons/postprocessing/OutputPass.js';

/** Reusable black material used to hide non-emissive objects during bloom pass. */
const _BLACK = new THREE.MeshBasicMaterial({ color: 0x000000 });

/** Additively blends the bloom render target onto the main scene render. */
const _BlendShader = {
    uniforms: {
        baseTexture:  { value: null as THREE.Texture | null },
        bloomTexture: { value: null as THREE.Texture | null },
    },
    vertexShader: /* glsl */`
        varying vec2 vUv;
        void main() {
            vUv = uv;
            gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
    `,
    fragmentShader: /* glsl */`
        uniform sampler2D baseTexture;
        uniform sampler2D bloomTexture;
        varying vec2 vUv;
        void main() {
            gl_FragColor = texture2D(baseTexture, vUv) + vec4(texture2D(bloomTexture, vUv).rgb, 0.0);
        }
    `,
};

export class PostFx {
    private readonly _bloomComposer: EffectComposer;
    private readonly _finalComposer: EffectComposer;
    private readonly _scene: THREE.Scene;
    private _bloomPass: UnrealBloomPass;
    // Temp storage for swapped materials — reused each frame to avoid allocation
    private readonly _darkened = new Map<THREE.Mesh, THREE.Material | THREE.Material[]>();

    constructor(
        renderer: THREE.WebGLRenderer,
        scene:    THREE.Scene,
        camera:   THREE.Camera,
        width:    number,
        height:   number,
    ) {
        this._scene = scene;

        // ── Bloom composer ─────────────────────────────────────────────────
        // Renders only emissive objects (everything else is black).
        this._bloomComposer = new EffectComposer(renderer);
        this._bloomComposer.renderToScreen = false;
        this._bloomComposer.addPass(new RenderPass(scene, camera));
        const bloom = new UnrealBloomPass(
            new THREE.Vector2(width, height),
            0.55,   // strength — can afford higher since only emissives trigger it
            0.6,    // radius   — glow spread
            0.0,    // threshold — 0 catches everything non-black (i.e. emissives after darken)
        );
        this._bloomPass = bloom;
        this._bloomComposer.addPass(bloom);

        // ── Final composer ─────────────────────────────────────────────────
        // Full scene + blend bloom + ACES output (SAOPass removed — halos on terrain)
        // ShaderPass: 'baseTexture' is auto-set to the previous pass's output
        const blendPass = new ShaderPass(_BlendShader, 'baseTexture');
        blendPass.uniforms['bloomTexture']!.value = this._bloomComposer.renderTarget2!.texture;

        this._finalComposer = new EffectComposer(renderer);
        this._finalComposer.addPass(new RenderPass(scene, camera));
        this._finalComposer.addPass(blendPass);
        this._finalComposer.addPass(new OutputPass());
    }

    render(): void {
        // 1. Darken all non-emissive meshes to isolate emissive bloom sources
        this._scene.traverse(obj => {
            if (!(obj as THREE.Mesh).isMesh) return;
            const mesh = obj as THREE.Mesh;
            const mat  = mesh.material;
            // MeshStandardMaterial has emissiveIntensity; others (Sky ShaderMaterial, LineBasicMaterial) do not
            const isEmissive = !Array.isArray(mat)
                && (mat as THREE.MeshStandardMaterial).emissiveIntensity != null
                && (mat as THREE.MeshStandardMaterial).emissiveIntensity > 0;
            if (!isEmissive) {
                this._darkened.set(mesh, mat);
                mesh.material = _BLACK;
            }
        });

        // 2. Render bloom (only emissive sources visible, everything else black)
        this._bloomComposer.render();

        // 3. Restore all materials
        for (const [mesh, mat] of this._darkened) mesh.material = mat;
        this._darkened.clear();

        // 4. Render full scene + blend bloom additively + ACES tone map
        this._finalComposer.render();
    }

    setSize(width: number, height: number): void {
        this._bloomComposer.setSize(width, height);
        this._finalComposer.setSize(width, height);
    }

    setBloomStrength(v: number): void { this._bloomPass.strength = v; }
    setBloomEnabled(v: boolean): void { this._bloomPass.enabled = v; }
}
