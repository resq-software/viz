// ResQ Viz - Terrain: heightmap ground + procedural obstacles
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

/** Terrain height at world position (x, z). Max amplitude ≈ 7.3m — smooth rolling countryside. */
export function terrainHeight(x: number, z: number): number {
    return Math.sin(x * 0.010) * Math.cos(z * 0.008) * 4.5   // wide gentle ridges
         + Math.sin(x * 0.025 + 1.7) * Math.cos(z * 0.022 + 0.9) * 2.0   // medium undulations
         + Math.sin(x * 0.055 + 3.2) * Math.cos(z * 0.048 + 2.1) * 0.8;  // subtle texture
}

// ── GLSL noise helpers injected into terrain shader ──────────────────────────

/** Declared in both vertex and fragment shaders via `#include <common>` replacement. */
const GLSL_VARYING = `
varying vec3 vTerrainWorld;
`;

/**
 * Injected at vertex `#include <begin_vertex>` to compute world-space position
 * without disturbing Three.js's internal `transformed` variable.
 */
const GLSL_VERT_WORLDPOS = `
vTerrainWorld = (modelMatrix * vec4(position, 1.0)).xyz;
`;

/**
 * Noise + biome fragment shader — replaces diffuseColor after Three.js resolves
 * its own color/map chunks.  All values are in the same colour space as Three.js
 * vertex colours (display-referred, matching how MeshStandardMaterial treats them).
 *
 * Biome palette (valley → low → mid → ridge):
 *   Valley  0x1a2d12  rgb(26,45,18)
 *   Low     0x2a4a1a  rgb(42,74,26)
 *   Mid     0x3a6028  rgb(58,96,40)
 *   Ridge   0x556448  rgb(85,100,72)
 */
const GLSL_FRAG_NOISE = `
// ── value noise ─────────────────────────────────────────────────────────────
float _ht(vec2 p) {
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
float _vn(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(_ht(i), _ht(i+vec2(1,0)), u.x),
               mix(_ht(i+vec2(0,1)), _ht(i+vec2(1,1)), u.x), u.y);
}
// 4-octave fractional Brownian motion
float _fbm(vec2 p) {
    float v=0.0; float a=0.5;
    for (int k=0;k<4;k++) { v+=a*_vn(p); p*=2.13; a*=0.5; }
    return v;
}
`;

/** Replaces `#include <color_fragment>` to drive diffuseColor procedurally. */
const GLSL_BIOME = `
{
    vec2 xz = vTerrainWorld.xz;

    // Two noise layers: large biome patches + micro detail
    float n  = _fbm(xz * 0.0085);                // biome-scale blobs
    float nd = _fbm(xz * 0.048 + vec2(7.31, 13.47)); // detail variation

    // Height zone [0=valley, 1=ridge] with noise-driven perturbation for
    // organic-looking biome boundaries (not purely altitude-stratified).
    float zone = clamp((vTerrainWorld.y + 7.5) / 15.0 + (n - 0.5) * 0.30, 0.0, 1.0);

    // Biome palette — four keyframes, smooth piecewise lerp
    vec3 cValley = vec3(0.102, 0.176, 0.071);   // 0x1a2d12  damp valley
    vec3 cLow    = vec3(0.165, 0.290, 0.102);   // 0x2a4a1a  low slope
    vec3 cMid    = vec3(0.227, 0.376, 0.157);   // 0x3a6028  mid slope
    vec3 cRidge  = vec3(0.333, 0.392, 0.282);   // 0x556448  rocky ridge

    vec3 biome;
    if (zone < 0.33)
        biome = mix(cValley, cLow,   zone / 0.33);
    else if (zone < 0.67)
        biome = mix(cLow,   cMid,   (zone - 0.33) / 0.34);
    else
        biome = mix(cMid,   cRidge, (zone - 0.67) / 0.33);

    // Micro-variation: lighten/darken patches by up to ±20%
    biome *= 0.82 + nd * 0.38;

    // Subtle warm↔cool tint driven by large-scale noise (makes distance
    // terrain look less uniform when viewed from above).
    biome = mix(biome * vec3(1.06, 1.0, 0.88),
                biome * vec3(0.92, 1.0, 1.04), n);

    diffuseColor.rgb = biome;
}
`;

/** Terrain is permanent for the lifetime of the app; no dispose() method is provided. */
export class Terrain {
    constructor(scene: THREE.Scene) {
        this._buildGround(scene);
        this._buildObstacles(scene);
        this._addNorthIndicator(scene);
        this._addOriginMarker(scene);
    }

    // ── Ground ────────────────────────────────────────────────────────────────

    private _buildGround(scene: THREE.Scene): void {
        const SIZE     = 1200;
        const SEGMENTS = 100;   // 100×100 = 10 000 quads → smooth hills
        const geo      = new THREE.PlaneGeometry(SIZE, SIZE, SEGMENTS, SEGMENTS);
        geo.rotateX(-Math.PI / 2);

        // Displace vertices on the CPU so shadow / physics queries match
        const pos = geo.attributes.position as THREE.BufferAttribute;
        for (let i = 0; i < pos.count; i++) {
            pos.setY(i, terrainHeight(pos.getX(i), pos.getZ(i)));
        }
        pos.needsUpdate = true;
        geo.computeVertexNormals();

        // ── Procedural terrain material ──────────────────────────────────────
        // Uses onBeforeCompile to inject value noise + height-based biome
        // blending directly into Three.js's MeshStandardMaterial PBR pipeline.
        // All lighting (shadows, AO, reflections) is preserved — only the
        // diffuseColor is computed procedurally in the fragment shader.
        const mat = new THREE.MeshStandardMaterial({
            color:     0xffffff,   // white — biome GLSL overrides diffuseColor
            roughness: 0.92,
            metalness: 0.0,
        });

        mat.onBeforeCompile = (shader) => {
            // ── Vertex shader: declare + populate vTerrainWorld varying ──────
            shader.vertexShader = shader.vertexShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}`,
            );
            shader.vertexShader = shader.vertexShader.replace(
                '#include <begin_vertex>',
                `#include <begin_vertex>\n${GLSL_VERT_WORLDPOS}`,
            );

            // ── Fragment shader: declare varying + inject noise helpers ───────
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}\n${GLSL_FRAG_NOISE}`,
            );

            // Replace the colour-application chunk with our biome computation
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <color_fragment>',
                GLSL_BIOME,
            );
        };

        // Cache key so the shader isn't accidentally shared with other meshes
        mat.customProgramCacheKey = () => 'terrain-procedural-v1';

        const mesh = new THREE.Mesh(geo, mat);
        mesh.receiveShadow = true;
        mesh.castShadow    = false;
        scene.add(mesh);
    }

    // ── Obstacles ─────────────────────────────────────────────────────────────

    private _buildObstacles(scene: THREE.Scene): void {
        // Seeded deterministic pseudo-random
        let seed = 42;
        const rng = () => { seed = (seed * 1664525 + 1013904223) & 0xffffffff; return (seed >>> 0) / 0xffffffff; };

        // --- Trees (20 instances) ---
        const trunkGeo  = new THREE.CylinderGeometry(0.4, 0.6, 6, 8);
        const canopyGeo = new THREE.SphereGeometry(3.5, 8, 6);
        const trunkMat  = new THREE.MeshStandardMaterial({ color: 0x4a3728, roughness: 0.95, metalness: 0 });
        const canopyMat = new THREE.MeshStandardMaterial({ color: 0x2d5a1b, roughness: 0.9,  metalness: 0 });

        for (let i = 0; i < 20; i++) {
            const cluster = i < 12 ? { cx: -180, cz: 120 } : { cx: 200, cz: -150 };
            const ox = (rng() - 0.5) * 120 + cluster.cx;
            const oz = (rng() - 0.5) * 120 + cluster.cz;
            const h  = terrainHeight(ox, oz);

            const trunk = new THREE.Mesh(trunkGeo, trunkMat);
            trunk.position.set(ox, h + 3, oz);
            trunk.castShadow    = true;
            trunk.receiveShadow = true;

            const canopy = new THREE.Mesh(canopyGeo, canopyMat);
            canopy.position.set(ox, h + 8.5 + rng() * 1.5, oz);
            canopy.castShadow    = true;
            canopy.receiveShadow = true;

            scene.add(trunk, canopy);
        }

        // --- Buildings (6 instances) in a small settlement ---
        const buildingMat = new THREE.MeshStandardMaterial({
            color:           0x2e2b27,
            roughness:       0.85,
            metalness:       0.0,
            envMapIntensity: 0.3,
        });
        const roofMat = new THREE.MeshStandardMaterial({
            color:           0x3d2018,
            roughness:       0.85,
            metalness:       0,
            envMapIntensity: 0.3,
        });

        const settlement = { cx: 80, cz: 80 };
        for (let i = 0; i < 6; i++) {
            const bx = settlement.cx + (rng() - 0.5) * 80;
            const bz = settlement.cz + (rng() - 0.5) * 80;
            const bh = terrainHeight(bx, bz);
            const w  = 8  + rng() * 8;
            const d  = 8  + rng() * 6;
            const ht = 6  + rng() * 10;

            const wall = new THREE.Mesh(new THREE.BoxGeometry(w, ht, d), buildingMat);
            wall.position.set(bx, bh + ht / 2, bz);
            wall.castShadow    = true;
            wall.receiveShadow = true;

            const roof = new THREE.Mesh(new THREE.BoxGeometry(w + 0.5, 2, d + 0.5), roofMat);
            roof.position.set(bx, bh + ht + 1, bz);
            roof.castShadow = true;

            scene.add(wall, roof);
        }
    }

    // ── Scene markers ─────────────────────────────────────────────────────────

    private _addNorthIndicator(scene: THREE.Scene): void {
        const dir = new THREE.Vector3(0, 0, -1).normalize();
        const arrow = new THREE.ArrowHelper(dir, new THREE.Vector3(0, 1, 0), 40, 0xff4444, 10, 5);
        scene.add(arrow);
    }

    private _addOriginMarker(scene: THREE.Scene): void {
        const mat = new THREE.LineBasicMaterial({ color: 0x444d56 });
        const pts  = [new THREE.Vector3(-20, 0.1, 0), new THREE.Vector3(20, 0.1, 0)];
        const pts2 = [new THREE.Vector3(0, 0.1, -20), new THREE.Vector3(0, 0.1,  20)];
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts),  mat));
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts2), mat));
    }
}
