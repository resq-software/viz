// ResQ Viz - Post-processing effects pipeline (selective bloom)
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { EffectComposer }  from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass }      from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { ShaderPass }      from 'three/addons/postprocessing/ShaderPass.js';
import { OutputPass }      from 'three/addons/postprocessing/OutputPass.js';
import { GTAOPass }        from 'three/addons/postprocessing/GTAOPass.js';

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
    private _gtaoPass: GTAOPass;
    // Temp storage for swapped materials — reused each frame to avoid allocation
    private readonly _darkened = new Map<THREE.Mesh, THREE.Material | THREE.Material[]>();
    // Sprites hidden during the bloom pass so HUD labels aren't additively
    // doubled (washed/blurred) by the blend. Restored after the bloom render.
    private readonly _hiddenSprites: THREE.Sprite[] = [];

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
        // Ground-truth ambient occlusion on the full lit scene, inserted BEFORE
        // the additive bloom blend so AO deepens crevices without dimming the
        // emissive nav-lights. GTAO (horizon-based) replaces the old SAOPass,
        // whose screen-space radius haloed terrain silhouettes. screenSpaceRadius
        // keeps the AO kernel stable across this scene's 0.5–40000 m depth range.
        const gtao = new GTAOPass(scene, camera, width, height);
        gtao.output = GTAOPass.OUTPUT.Default;
        gtao.updateGtaoMaterial({
            radius:            0.5,
            distanceExponent:  1.0,
            thickness:         1.0,
            scale:             1.0,
            samples:           16,
            distanceFallOff:   1.0,
            screenSpaceRadius: true,
        });
        this._gtaoPass = gtao;
        this._finalComposer.addPass(gtao);
        this._finalComposer.addPass(blendPass);
        this._finalComposer.addPass(new OutputPass());
    }

    render(): void {
        // 1. Darken all non-emissive meshes to isolate emissive bloom sources
        this._scene.traverse(obj => {
            // Sprites (id labels) aren't meshes, so the darken pass below skips
            // them — they'd render at full colour into the bloom target and the
            // additive blend would double them, washing the text out. Hide them
            // for the bloom render; the final composer still draws them once.
            if ((obj as THREE.Sprite).isSprite) {
                if (obj.visible) {
                    obj.visible = false;
                    this._hiddenSprites.push(obj as THREE.Sprite);
                }
                return;
            }
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

        // 3. Restore all materials + sprite visibility
        for (const [mesh, mat] of this._darkened) mesh.material = mat;
        this._darkened.clear();
        for (const s of this._hiddenSprites) s.visible = true;
        this._hiddenSprites.length = 0;

        // 4. Render full scene + blend bloom additively + ACES tone map
        this._finalComposer.render();
    }

    setSize(width: number, height: number): void {
        this._bloomComposer.setSize(width, height);
        this._finalComposer.setSize(width, height);
        this._gtaoPass.setSize(width, height);
    }

    setBloomStrength(v: number): void { this._bloomPass.strength = v; }
    setBloomEnabled(v: boolean): void { this._bloomPass.enabled = v; }
    setSsaoEnabled(v: boolean): void  { this._gtaoPass.enabled = v; }
    /** Blend strength of the AO term (0 = none, 1 = full). */
    setSsaoIntensity(v: number): void { this._gtaoPass.blendIntensity = v; }
}
