// Copyright 2026 ResQ Systems, Inc.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

import * as THREE from 'three';
import { Water } from 'three/addons/objects/Water.js';
import { loadTexture } from './assetLoader';
import { sunDirection, SUN_COLOR, DEFAULT_SUN_ELEVATION_DEG, DEFAULT_SUN_AZIMUTH_DEG } from './lighting';
import { getLogger } from './log';

const log = getLogger('water');

// Reflective Water surface lifecycle — owns the Water instance, normal-map
// hot-swap, and per-frame uniform tick. Extracted from terrain.ts so the
// Three.js water addon and texture-loading state stay separate from terrain
// mesh generation.

const _normalsPlaceholder: THREE.Texture = (() => {
    // 1×1 white seed so the Water uniform slot is non-null until the real
    // normals texture finishes loading. The Water addon takes its normal map
    // at construction time; the swap below avoids a material recompile.
    const data = new Uint8Array([255, 255, 255, 255]);
    const tex = new THREE.DataTexture(data, 1, 1, THREE.RGBAFormat);
    tex.needsUpdate = true;
    return tex;
})();

let _instance: Water | null = null;
let _cachedNormals: THREE.Texture | null = null;
let _normalsLoadStarted = false;

/**
 * Grid resolution of the water plane when a depth sampler is supplied.
 *
 * 256 gives one vertex every ~15 m across the 4 km world — finer than the
 * shoreline needs, coarse enough that the whole surface is ~66 k vertices.
 */
const SHORE_SEGMENTS = 256;

/**
 * Metres of water over which the surface fades in from the shore.
 *
 * The one number that decides whether a lake reads as a body of water or as
 * spilled paint. Too small and the hard edge this exists to remove comes back;
 * too large and the middle of a shallow lake is see-through.
 */
const SHORE_FADE_M = 6.0;

/** Metres of depth over which the shallow tint gives way to the deep colour. */
const SHALLOW_DEPTH_M = 22.0;

// The three constants below are deliberately GENTLE, and that is a judgement about
// where they were verified rather than about what looks best.
//
// They were tuned against a headless SwiftShader capture, which is not a faithful
// preview of this scene: it has no working image-based lighting (a sky-driven PMREM
// environment renders flat there), so the very sky blow-out these damp is partly an
// artefact of the capture rather than of the renderer a user has. Values aggressive
// enough to "fix" the headless image took the lake down to the same olive as the
// land — worse, on hardware that never had the problem.
//
// So each is set just far enough from its neutral value to stop genuine clipping,
// and no further. The structural fixes in this file — the shore fade and the
// shallow-water tint — need no such hedging: they are geometry and depth, and they
// are correct on any renderer. These three are worth a look on real hardware.

/**
 * Ceiling on the addon's Fresnel reflectance.
 *
 * Just under 1, so distant water keeps a little of its own colour rather than
 * becoming a pure mirror of the sky and clipping through ACES, while the
 * grazing-angle brightening every real body of water shows is untouched.
 */
const MAX_REFLECTANCE = 0.95;

/** How much of the water's body colour is mixed into what it reflects. */
const REFLECT_TINT = 0.08;

/**
 * Scale on the addon's specular term, whose exponent and strength are baked into
 * its shader (`shiny 100, spec 2`) and are not configurable. At full strength the
 * sun's glint punches a hole through the surface at this scene's exposure.
 */
const SPECULAR_SCALE = 0.65;

/**
 * Writes each vertex's water depth into an `aDepth` attribute.
 *
 * Depth, not height: the shader wants "how much water stands here", which is
 * zero on dry land and grows as the bed falls away. Clamped at zero so terrain
 * standing proud of the water level does not drive the fade negative.
 */
function _attachDepth(
    geo: THREE.BufferGeometry,
    waterLevel: number,
    sampler?: (x: number, z: number) => number,
): void {
    const pos = geo.getAttribute('position');
    const depth = new Float32Array(pos.count);
    if (sampler) {
        for (let i = 0; i < pos.count; i++) {
            const d = waterLevel - sampler(pos.getX(i), pos.getZ(i));
            depth[i] = d > 0 ? d : 0;
        }
    } else {
        // No sampler: every vertex is "deep", so the fade is a no-op and the
        // surface renders exactly as it did before this existed.
        depth.fill(SHALLOW_DEPTH_M);
    }
    geo.setAttribute('aDepth', new THREE.BufferAttribute(depth, 1));
}

/**
 * Patches the addon's shaders in place to fade the surface out at the shoreline
 * and lift shallow water toward a shallower colour.
 *
 * Why patch rather than configure: the addon mixes a Fresnel reflectance
 * between `waterColor` and the mirror sample, and that mix is the whole
 * appearance. At a grazing angle reflectance approaches 1 and the surface shows
 * the raw sky; looking down it approaches 0.02 and shows `waterColor`, which the
 * presets set very dark. The result was a lake rendered as two materials with a
 * hard diagonal terminator between them — a bright half and a near-black half —
 * plus an aliased edge wherever the plane cut the terrain. Nothing in the
 * addon's options reaches any of that.
 *
 * Called before the material has ever been compiled, so mutating the shader
 * strings is enough and no recompile has to be forced.
 */
function _patchShoreShading(water: Water): void {
    const mat = water.material as THREE.ShaderMaterial;

    mat.uniforms['uShoreFade'] = { value: SHORE_FADE_M };
    mat.uniforms['uShallowDepth'] = { value: SHALLOW_DEPTH_M };
    // A desaturated, lighter version of the deep colour rather than a fixed
    // tint, so every preset's water keeps its own identity in the shallows.
    mat.uniforms['uShallowColor'] = { value: new THREE.Color(0x2f6f7a) };
    mat.uniforms['uMaxReflect'] = { value: MAX_REFLECTANCE };
    mat.uniforms['uReflectTint'] = { value: REFLECT_TINT };
    mat.uniforms['uSpecular'] = { value: SPECULAR_SCALE };

    mat.vertexShader = mat.vertexShader
        .replace(
            'uniform float time;',
            'uniform float time;\nattribute float aDepth;\nvarying float vDepth;',
        )
        .replace(
            'mirrorCoord = modelMatrix * vec4( position, 1.0 );',
            'vDepth = aDepth;\n\t\t\t\t\tmirrorCoord = modelMatrix * vec4( position, 1.0 );',
        );

    mat.fragmentShader = mat.fragmentShader
        .replace(
            'uniform vec3 waterColor;',
            'uniform vec3 waterColor;\nuniform float uShoreFade;\nuniform float uShallowDepth;\nuniform vec3 uShallowColor;\nvarying float vDepth;',
        )
        // Shallow water is lighter, because less of the column absorbs the light
        // coming back out of it. Without this the shallows are the same near-black
        // as the deep, which is what made a lake read as one flat cut-out.
        //
        // (Every replacement below emits terse GLSL on purpose: shader source is a
        // string literal, so a comment written inside one is shipped to every
        // client in the entry bundle, where the budget is measured. The reasoning
        // lives out here, where the minifier strips it.)
        .replace(
            'vec3 scatter = max( 0.0, dot( surfaceNormal, eyeDirection ) ) * waterColor;',
            'float shoal = 1.0 - clamp( vDepth / uShallowDepth, 0.0, 1.0 );\n'
            + 'vec3 bodyColor = mix( waterColor, uShallowColor, shoal * shoal );\n'
            + 'vec3 scatter = max( 0.0, dot( surfaceNormal, eyeDirection ) ) * bodyColor;',
        )
        // Two corrections to the addon's mix, both about the far half of a lake.
        // Fresnel drives reflectance to 1 at a grazing angle, so distant water
        // showed the sky sample raw. Physically that is what water does; through
        // ACES at this scene's exposure it clipped to a flat fog-white sheet, and
        // the lake read as two materials meeting at a hard diagonal rather than as
        // one body. Capping reflectance keeps the gradient while leaving the water
        // some colour of its own everywhere, and tinting the sample toward that
        // colour is what a deep body does to the light it bounces back.
        .replace(
            'vec3 albedo = mix( ( sunColor * diffuseLight * 0.3 + scatter ) * getShadowMask(), reflectionSample + specularLight, reflectance );',
            'vec3 albedo = mix( ( sunColor * diffuseLight * 0.3 + scatter ) * getShadowMask(),'
            + ' mix( reflectionSample, bodyColor, uReflectTint ) + specularLight * uSpecular,'
            + ' min( reflectance, uMaxReflect ) );',
        )
        // Fade out as the bed rises to meet the surface, so the plane stops ending
        // in a hard aliased line against the terrain it intersects.
        .replace(
            'gl_FragColor = vec4( outgoingLight, alpha );',
            'gl_FragColor = vec4( outgoingLight, alpha * smoothstep( 0.0, uShoreFade, vDepth ) );',
        );
}

// Canonical sun direction, kept at module scope so the value scene.ts pushes
// during init (via updateWaterSunDirection, before any Water exists) survives
// until the terrain builds the Water instance — and persists across the
// preset-driven water rebuilds that would otherwise reset it to a default.
// Seeded from the same helper the scene uses, so the pre-init fallback matches
// the real default. The previous literal (0.45, 0.88, 0.25) normalised to
// ~(0.443, 0.867, 0.246), whose z is the opposite sign to
// sunDirection(40, 135) ~ (0.542, 0.643, -0.542) — the glint pointed across the
// scene from the Sky sun until the first updateWaterSunDirection call.
const _sunDir = sunDirection(DEFAULT_SUN_ELEVATION_DEG, DEFAULT_SUN_AZIMUTH_DEG);

async function _loadNormals(): Promise<void> {
    if (_normalsLoadStarted || _cachedNormals) return;
    _normalsLoadStarted = true;
    try {
        const tex = await loadTexture('/textures/waternormals.jpg');
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.RepeatWrapping;
        // Cache so subsequent buildWaterMesh calls reuse the loaded texture
        // instead of reverting to the placeholder during preset rebuilds.
        // Read _instance once after await so a swap mid-load can't leave a
        // dropped texture or hit a disposed instance.
        _cachedNormals = tex;
        const target = _instance;
        if (target) {
            const u = target.material.uniforms['normalSampler'];
            if (u) u.value = tex;
        }
    } catch (err) {
        log.warn('water normals load failed, keeping flat water', { err });
    }
}

/**
 * Build the reflective water plane for the active terrain preset. Registers
 * the result as the active instance so {@link tickWater} can advance its
 * shader clock, and kicks off the lazy normals load.
 *
 * Caller is responsible for adding the returned mesh to the scene and for
 * invoking {@link disposeWaterMesh} when the terrain rebuilds.
 */
export function buildWaterMesh(opts: {
    size: number;
    waterLevel: number;
    fog: boolean;
    waterColor?: number;
    /**
     * Terrain height at a world point, used to give each vertex the depth of
     * water standing over it. Omit it and the surface renders at full opacity
     * everywhere, exactly as it did before depth existed.
     */
    depthSampler?: (x: number, z: number) => number;
}): Water {
    // Subdivided, where this used to be two triangles. The shore fade reads a
    // per-vertex depth, and a single quad can only interpolate between its four
    // corners — which puts the "shoreline" halfway across the map. The extra
    // vertices are a rounding error beside the terrain mesh and they buy a
    // shoreline that follows the actual coast.
    const segments = opts.depthSampler ? SHORE_SEGMENTS : 1;
    const geo = new THREE.PlaneGeometry(opts.size, opts.size, segments, segments);
    geo.rotateX(-Math.PI / 2);
    _attachDepth(geo, opts.waterLevel, opts.depthSampler);

    const water = new Water(geo, {
        // 512² reflection (was 256²) — sharper mirror, cheap on a modern GPU.
        textureWidth:    512,
        textureHeight:   512,
        waterNormals:    _cachedNormals ?? _normalsPlaceholder,
        // Shared canonical sun so the water's specular glint lands where the
        // visible Sky sun and terrain shadows say it should (see ./lighting).
        // Use the canonical direction, not a recomputed default: scene.ts may
        // already have applied a scenario sun via setSunPosition, and a
        // preset switch rebuilds this mesh. Recomputing the default here made
        // the glint disagree with the Sky and the shadows until the next
        // setSunPosition. Cloned so the addon cannot alias the module vector.
        sunDirection:    _sunDir.clone(),
        sunColor:        SUN_COLOR,
        // Caller override kept from main; default is the WIP's deep teal.
        waterColor:      opts.waterColor ?? 0x0e2a3d,
        // More distortion so the broken-up reflection actually shimmers.
        distortionScale: 3.6,
        fog:             opts.fog,
    });
    // `size` sets ripple frequency (normal map tiles every ~103/size world-m).
    // The addon reads it as a uniform but omits it from its TS options type, so
    // set it directly. Default 1.0 = ~103 m swells (a mirror at altitude);
    // 6.0 → ~17 m chop that breaks the reflection into believable surface.
    const _size = water.material.uniforms['size'];
    if (_size) _size.value = 6.0;

    if (opts.depthSampler) {
        _patchShoreShading(water);
        // The shore fade writes a varying alpha, which does nothing at all
        // unless the material actually blends. The addon leaves `transparent`
        // false because its own alpha is a constant 1.
        const mat = water.material as THREE.ShaderMaterial;
        mat.transparent = true;
        // Keep writing depth: the surface is still an opaque body of water for
        // everything below it, and dropping depth writes let submerged terrain
        // sort through the middle of the lake.
        mat.depthWrite = true;
    }

    water.position.y = opts.waterLevel;
    _instance = water;
    if (!_cachedNormals) void _loadNormals();
    return water;
}

/**
 * Advance the Water shader clock from the render-loop tick callback.
 * Without this the reflective ripple is static.
 */
export function tickWater(dt: number): void {
    if (_instance) {
        const u = _instance.material.uniforms['time'];
        if (u) u.value = (u.value as number) + dt;
    }
}

/**
 * Update the sun direction vector on the active water instance.
 */
export function updateWaterSunDirection(sunDir: THREE.Vector3): void {
    // Record canonically first so a not-yet-built (or rebuilt) Water instance
    // still picks up the right glint direction at construction time.
    _sunDir.copy(sunDir).normalize();
    if (_instance) {
        const u = _instance.material.uniforms['sunDirection'];
        if (u) {
            (u.value as THREE.Vector3).copy(_sunDir);
        }
    }
}

/**
 * Clear the active Water reference so {@link tickWater} no longer mutates a
 * disposed instance. Called from the owning terrain's dispose path before a
 * new instance is constructed.
 */
export function disposeWaterMesh(): void {
    // The addon allocates a half-float WebGLRenderTarget for the mirror pass and
    // never exposes it, so the terrain's own dispose sweep — which frees the
    // geometry and material of every mesh it owns — could not reach it, and each
    // preset switch stranded another 512² reflection buffer on the GPU. The
    // texture is reachable through the uniform it is bound to, and disposing it
    // frees the allocation the buffer is actually made of.
    //
    // The framebuffer object itself stays until the renderer is torn down: it is
    // keyed off the render target, and the addon keeps that in a closure with no
    // reference out. Freeing the texture is the part that is worth megabytes.
    const mirror = _instance?.material.uniforms['mirrorSampler']?.value as
        THREE.Texture | undefined;
    mirror?.dispose();
    _instance = null;
}
