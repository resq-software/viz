// ResQ Viz - Height-falloff atmospheric fog (global ShaderChunk override)
// SPDX-License-Identifier: Apache-2.0
//
// `THREE.FogExp2` applies identical extinction to the near field and the
// horizon, so raising density to make distant terrain recede also lays a milky
// veil over everything two metres from the camera and desaturates the whole
// frame. Real aerial perspective is height-dependent — haze pools in valleys,
// thins with altitude — and forward-scattering-dependent: it brightens toward
// the sun. This module replaces the four fog chunks globally so every
// fog-enabled material gets both, with no per-material plumbing.
//
// Three constraints read off the shipped shaders, all load-bearing:
//
//   • `Sky.js` contains zero occurrences of `fog`, so the sky dome is untouched
//     by this override — correct, the dome must not be fogged.
//   • `Water.js` DOES consume the chunks (`fog_pars_vertex` :120, `fog_vertex`
//     :134, `fog_fragment` :209) but its vertex shader never defines
//     `transformed` — it transforms `position` directly at :127-128. An override
//     written against `transformed`, the obvious implementation, fails to
//     compile Water. World position is therefore derived from the raw `position`
//     attribute, which every material has. Water also already declares
//     `varying vec4 worldPosition`, hence the `vFog`-prefixed names here.
//   • `terrain.ts:314-315` declares `vTerrainWorld` / `vWorldNormal`. This module
//     is deliberately self-contained rather than borrowing either, so there is
//     no name collision to break terrain compilation.
//
// With `fogHeightFalloff = 0` the height term collapses to exactly 1 and the
// result matches stock `FogExp2` — the override is a strict superset, so nothing
// regresses if an environment opts out.

import * as THREE from 'three';

/** Tunable parameters of the height-fog model. */
export interface HeightFogParams {
    /** Base extinction at y = 0. Same units as `FogExp2.density`. */
    readonly density?: number;
    /** Fog colour at the horizon, away from the sun. */
    readonly color?: THREE.ColorRepresentation;
    /** Vertical falloff, 1/metres. 0 = uniform (stock FogExp2 behaviour). */
    readonly heightFalloff?: number;
    /** Unit vector toward the sun, for forward scattering. */
    readonly sunDirection?: THREE.Vector3;
    /** Colour the fog takes when looking into the sun. */
    readonly sunColor?: THREE.ColorRepresentation;
    /** 0 = no forward scattering, 1 = full. */
    readonly sunIntensity?: number;
}

/**
 * Authoritative parameter values. Materials each hold their own uniform objects
 * (three's `UniformsUtils.merge` clones them), so these are the source of truth
 * and {@link setHeightFogParams} pushes them into live materials.
 */
const _state = {
    heightFalloff: 0,
    sunDirection:  new THREE.Vector3(0, 1, 0),
    sunColor:      new THREE.Color(0xffffff),
    sunIntensity:  0,
};

let _installed = false;

const FOG_PARS_VERTEX = /* glsl */`
#ifdef USE_FOG
	varying float vFogDepth;
	varying vec3  vFogWorldPos;
#endif
`;

// NOTE: uses `position`, never `transformed` — see the module header for why.
// `instanceMatrix` is applied explicitly because three applies it in
// `project_vertex`, i.e. it is not folded into `transformed` either.
const FOG_VERTEX = /* glsl */`
#ifdef USE_FOG
	vFogDepth = - mvPosition.z;
	vec4 _fogLocal = vec4( position, 1.0 );
	#ifdef USE_INSTANCING
		_fogLocal = instanceMatrix * _fogLocal;
	#endif
	vFogWorldPos = ( modelMatrix * _fogLocal ).xyz;
#endif
`;

const FOG_PARS_FRAGMENT = /* glsl */`
#ifdef USE_FOG
	uniform vec3  fogColor;
	varying float vFogDepth;
	varying vec3  vFogWorldPos;
	#ifdef FOG_EXP2
		uniform float fogDensity;
	#else
		uniform float fogNear;
		uniform float fogFar;
	#endif
	uniform float fogHeightFalloff;
	uniform vec3  fogSunDirection;
	uniform vec3  fogSunColor;
	uniform float fogSunIntensity;
#endif
`;

const FOG_FRAGMENT = /* glsl */`
#ifdef USE_FOG
	#ifdef FOG_EXP2
		// Analytic integral of exp(-k·y) along the view ray, normalised so the
		// k -> 0 limit is exactly 1 and the model degrades to stock FogExp2.
		float _fogDy  = vFogWorldPos.y - cameraPosition.y;
		float _fogKdy = fogHeightFalloff * _fogDy;
		float _fogH;
		// The integral is singular as the ray goes horizontal (_fogKdy -> 0),
		// which is precisely the overview shot. Series-expand through it.
		if ( abs( _fogKdy ) < 1e-4 ) {
			_fogH = 1.0 - 0.5 * _fogKdy;
		} else {
			_fogH = ( 1.0 - exp( - _fogKdy ) ) / _fogKdy;
		}
		_fogH *= exp( - fogHeightFalloff * cameraPosition.y );
		float _fogDist  = vFogDepth * max( _fogH, 0.0 );
		float fogFactor = 1.0 - exp( - fogDensity * fogDensity * _fogDist * _fogDist );
	#else
		float fogFactor = smoothstep( fogNear, fogFar, vFogDepth );
	#endif
	// Forward scattering: haze brightens toward the sun. Without this the model
	// is just tinted uniform fog, not aerial perspective.
	vec3  _fogView = normalize( vFogWorldPos - cameraPosition );
	float _fogSun  = max( dot( _fogView, fogSunDirection ), 0.0 );
	vec3  _fogCol  = mix( fogColor, fogSunColor, fogSunIntensity * pow( _fogSun, 4.0 ) );
	gl_FragColor.rgb = mix( gl_FragColor.rgb, _fogCol, saturate( fogFactor ) );
#endif
`;

/**
 * Replace the fog chunks and extend `UniformsLib.fog`.
 *
 * MUST run before any fog-enabled material is constructed — three snapshots
 * `UniformsLib.fog` into each material at creation, so a material built earlier
 * would lack the new uniforms and fail to link.
 *
 * Idempotent: safe to call more than once.
 */
export function installHeightFog(): void {
    if (_installed) return;
    _installed = true;

    THREE.ShaderChunk['fog_pars_vertex']   = FOG_PARS_VERTEX;
    THREE.ShaderChunk['fog_vertex']        = FOG_VERTEX;
    THREE.ShaderChunk['fog_pars_fragment'] = FOG_PARS_FRAGMENT;
    THREE.ShaderChunk['fog_fragment']      = FOG_FRAGMENT;

    const fogUniforms = THREE.UniformsLib['fog'] as Record<string, THREE.IUniform>;
    fogUniforms['fogHeightFalloff'] = { value: _state.heightFalloff };
    fogUniforms['fogSunDirection']  = { value: _state.sunDirection.clone() };
    fogUniforms['fogSunColor']      = { value: _state.sunColor.clone() };
    fogUniforms['fogSunIntensity']  = { value: _state.sunIntensity };
}

/**
 * Update fog parameters and push them into every live material in `scene`.
 *
 * Materials hold cloned uniform objects, so both the `UniformsLib` defaults (for
 * materials created later) and the existing materials (for the current frame)
 * must be written. Scene walks are O(materials) and happen on environment
 * change only, never per frame.
 */
export function setHeightFogParams(scene: THREE.Scene, params: HeightFogParams): void {
    installHeightFog();

    if (params.heightFalloff !== undefined) _state.heightFalloff = params.heightFalloff;
    if (params.sunIntensity  !== undefined) _state.sunIntensity  = params.sunIntensity;
    if (params.sunDirection)                _state.sunDirection.copy(params.sunDirection).normalize();
    if (params.sunColor !== undefined)      _state.sunColor.set(params.sunColor);

    if (scene.fog instanceof THREE.FogExp2) {
        if (params.density !== undefined) scene.fog.density = params.density;
        if (params.color   !== undefined) scene.fog.color.set(params.color);
    }

    // Keep library defaults in step so materials created after this call start
    // with the current atmosphere rather than the boot-time one.
    const lib = THREE.UniformsLib['fog'] as Record<string, THREE.IUniform>;
    lib['fogHeightFalloff']!.value = _state.heightFalloff;
    (lib['fogSunDirection']!.value as THREE.Vector3).copy(_state.sunDirection);
    (lib['fogSunColor']!.value as THREE.Color).copy(_state.sunColor);
    lib['fogSunIntensity']!.value = _state.sunIntensity;

    scene.traverse(obj => {
        const mesh = obj as THREE.Mesh;
        if (!mesh.material) return;
        const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
        for (const mat of materials) applyToMaterial(mat);
    });
}

/**
 * Write the current fog state into one material's uniforms.
 *
 * Exported so materials built outside the scene graph — or rebuilt after a
 * preset switch — can be brought into step without a full traverse.
 */
export function applyToMaterial(material: THREE.Material): void {
    const uniforms = (material as THREE.ShaderMaterial).uniforms;
    if (!uniforms) return;
    if (uniforms['fogHeightFalloff']) uniforms['fogHeightFalloff'].value = _state.heightFalloff;
    if (uniforms['fogSunIntensity'])  uniforms['fogSunIntensity'].value  = _state.sunIntensity;
    if (uniforms['fogSunDirection'])  (uniforms['fogSunDirection'].value as THREE.Vector3).copy(_state.sunDirection);
    if (uniforms['fogSunColor'])      (uniforms['fogSunColor'].value as THREE.Color).copy(_state.sunColor);
}

/** Current parameter values — for tests and diagnostics. */
export function getHeightFogState(): Readonly<typeof _state> {
    return _state;
}
