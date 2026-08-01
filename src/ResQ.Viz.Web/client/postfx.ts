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

/**
 * Cinematic color grade — the final display-space pass (runs AFTER OutputPass,
 * so it operates on tone-mapped, sRGB-encoded colour, which is the correct space
 * for a filmic grade). Gives the whole scene a somber, weighty, "real place"
 * feel instead of flat game render: gentle contrast, mild desaturation, a
 * cool-shadow / warm-highlight split tone, a soft vignette, and fine animated
 * film grain. All tunable; keep it subtle so it enhances rather than distorts.
 */
const _GradeShader = {
    uniforms: {
        tDiffuse:      { value: null as THREE.Texture | null },
        uTime:         { value: 0 },
        uEnabled:      { value: 1 },
        uContrast:     { value: 1.065 },
        uSaturation:   { value: 0.92 },
        uShadowTint:   { value: new THREE.Color(0.015, 0.028, 0.052) }, // cool haze in shadows
        uHighlightTint:{ value: new THREE.Color(0.045, 0.028, 0.010) }, // warm sun in highlights
        uLift:         { value: 0.012 },   // atmospheric shadow lift
        uVignette:     { value: 0.34 },
        uGrain:        { value: 0.032 },
    },
    vertexShader: /* glsl */`
        varying vec2 vUv;
        void main() {
            vUv = uv;
            gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
    `,
    fragmentShader: /* glsl */`
        uniform sampler2D tDiffuse;
        uniform float uTime, uEnabled, uContrast, uSaturation, uLift, uVignette, uGrain;
        uniform vec3  uShadowTint, uHighlightTint;
        varying vec2 vUv;

        float _hash(vec2 p) {
            p = fract(p * vec2(123.34, 345.45));
            p += dot(p, p + 34.345);
            return fract(p.x * p.y);
        }

        void main() {
            vec3 c = texture2D(tDiffuse, vUv).rgb;
            if (uEnabled < 0.5) { gl_FragColor = vec4(c, 1.0); return; }

            // Contrast S-curve around mid grey.
            c = (c - 0.5) * uContrast + 0.5;

            // Luma-based saturation pull-down.
            float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
            c = mix(vec3(l), c, uSaturation);

            // Split tone: cool into shadows, warm into highlights.
            c += uShadowTint    * (1.0 - smoothstep(0.0, 0.55, l));
            c += uHighlightTint * smoothstep(0.45, 1.0, l);

            // Gentle shadow lift — reads as atmospheric haze filling the darks.
            c += uLift * (1.0 - l);

            // Soft vignette (1.0 at centre, dimming toward the corners).
            float vig = smoothstep(0.9, 0.25, length(vUv - 0.5));
            c *= mix(1.0, vig, uVignette);

            // Fine animated film grain — kills the sterile digital-flat look.
            // uTime is a per-frame integer counter, so the hash input shifts
            // each frame and the grain re-randomises.
            float g = _hash(vUv * vec2(1927.0, 1081.0) + uTime) - 0.5;
            c += g * uGrain;

            gl_FragColor = vec4(clamp(c, 0.0, 1.0), 1.0);
        }
    `,
};

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
    private _gradePass: ShaderPass;
    private _time = 0;
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

        // OutputPass tone-maps (ACES) + encodes to sRGB. The color grade runs
        // LAST, on that display-referred output — the correct space for a
        // filmic grade + vignette + grain. Being last, it renders to screen.
        const gradePass = new ShaderPass(_GradeShader);
        this._gradePass = gradePass;

        this._finalComposer = new EffectComposer(renderer);
        this._finalComposer.addPass(new RenderPass(scene, camera));
        this._finalComposer.addPass(blendPass);
        this._finalComposer.addPass(new OutputPass());
        this._finalComposer.addPass(gradePass);
    }

    render(): void {
        // Advance grain animation. A frame-tick counter is enough — the grain
        // only needs to change frame-to-frame, not track wall-clock time.
        this._time = (this._time + 1) % 1000;
        this._gradePass.uniforms['uTime']!.value = this._time;

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

    /** Toggle the cinematic color-grade pass (grade + vignette + grain). */
    setColorGradeEnabled(v: boolean): void {
        this._gradePass.uniforms['uEnabled']!.value = v ? 1 : 0;
    }
}
