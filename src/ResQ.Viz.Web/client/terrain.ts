// ResQ Viz - Terrain: heightmap ground + procedural obstacles
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

/** Terrain size in world units. */
const TERRAIN_SIZE = 4000;

/** Terrain height at world position (x, z). Max amplitude ≈ 7.3m — smooth rolling countryside. */
export function terrainHeight(x: number, z: number): number {
    return Math.sin(x * 0.010) * Math.cos(z * 0.008) * 4.5   // wide gentle ridges
         + Math.sin(x * 0.025 + 1.7) * Math.cos(z * 0.022 + 0.9) * 2.0   // medium undulations
         + Math.sin(x * 0.055 + 3.2) * Math.cos(z * 0.048 + 2.1) * 0.8;  // subtle texture
}

// ── GLSL noise helpers injected into terrain shader ──────────────────────────

const GLSL_VARYING = `
varying vec3 vTerrainWorld;
`;

const GLSL_VERT_WORLDPOS = `
vTerrainWorld = (modelMatrix * vec4(position, 1.0)).xyz;
`;

const GLSL_FRAG_NOISE = `
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
float _fbm(vec2 p) {
    float v=0.0; float a=0.5;
    for (int k=0;k<4;k++) { v+=a*_vn(p); p*=2.13; a*=0.5; }
    return v;
}
`;

const GLSL_BIOME = `
{
    vec2 xz = vTerrainWorld.xz;
    float n  = _fbm(xz * 0.0085);
    float nd = _fbm(xz * 0.048 + vec2(7.31, 13.47));

    float zone = clamp((vTerrainWorld.y + 7.5) / 15.0 + (n - 0.5) * 0.30, 0.0, 1.0);

    vec3 cValley = vec3(0.102, 0.176, 0.071);
    vec3 cLow    = vec3(0.165, 0.290, 0.102);
    vec3 cMid    = vec3(0.227, 0.376, 0.157);
    vec3 cRidge  = vec3(0.333, 0.392, 0.282);

    vec3 biome;
    if (zone < 0.33)
        biome = mix(cValley, cLow,   zone / 0.33);
    else if (zone < 0.67)
        biome = mix(cLow,   cMid,   (zone - 0.33) / 0.34);
    else
        biome = mix(cMid,   cRidge, (zone - 0.67) / 0.33);

    biome *= 0.82 + nd * 0.38;
    biome  = mix(biome * vec3(1.06, 1.0, 0.88), biome * vec3(0.92, 1.0, 1.04), n);

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
        // 160 segments over 4000 m → 25 m/quad; smooth enough for rolling hills
        const geo = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, 160, 160);
        geo.rotateX(-Math.PI / 2);

        const pos = geo.attributes.position as THREE.BufferAttribute;
        for (let i = 0; i < pos.count; i++) {
            pos.setY(i, terrainHeight(pos.getX(i), pos.getZ(i)));
        }
        pos.needsUpdate = true;
        geo.computeVertexNormals();

        const mat = new THREE.MeshStandardMaterial({
            color:     0xffffff,
            roughness: 0.92,
            metalness: 0.0,
        });

        mat.onBeforeCompile = (shader) => {
            shader.vertexShader = shader.vertexShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}`,
            );
            shader.vertexShader = shader.vertexShader.replace(
                '#include <begin_vertex>',
                `#include <begin_vertex>\n${GLSL_VERT_WORLDPOS}`,
            );
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}\n${GLSL_FRAG_NOISE}`,
            );
            shader.fragmentShader = shader.fragmentShader.replace(
                '#include <color_fragment>',
                GLSL_BIOME,
            );
        };
        mat.customProgramCacheKey = () => 'terrain-procedural-v1';

        const mesh = new THREE.Mesh(geo, mat);
        mesh.receiveShadow = true;
        scene.add(mesh);
    }

    // ── Obstacles — InstancedMesh for trees and buildings ─────────────────────
    //
    // Reduces obstacle draw calls from O(n) to 4 total (trunk, canopy, wall, roof).

    private _buildObstacles(scene: THREE.Scene): void {
        let seed = 42;
        const rng = (): number => {
            seed = (seed * 1664525 + 1013904223) & 0xffffffff;
            return (seed >>> 0) / 0xffffffff;
        };

        this._buildTrees(scene, rng);
        this._buildBuildings(scene, rng);
    }

    private _buildTrees(scene: THREE.Scene, rng: () => number): void {
        const COUNT = 40;

        // Clusters spread across the larger terrain
        const clusters = [
            { cx: -180, cz:  120, r: 120 },   // west grove
            { cx:  200, cz: -150, r: 120 },   // north-east stand
            { cx: -600, cz: -400, r: 160 },   // far south-west
            { cx:  800, cz:  600, r: 140 },   // far south-east
        ];

        const trunkGeo  = new THREE.CylinderGeometry(0.4, 0.6, 6, 8);
        const canopyGeo = new THREE.SphereGeometry(3.5, 8, 6);
        const trunkMat  = new THREE.MeshStandardMaterial({ color: 0x4a3728, roughness: 0.95, metalness: 0 });
        const canopyMat = new THREE.MeshStandardMaterial({ color: 0x2d5a1b, roughness: 0.9,  metalness: 0 });

        const trunks  = new THREE.InstancedMesh(trunkGeo,  trunkMat,  COUNT);
        const canopies = new THREE.InstancedMesh(canopyGeo, canopyMat, COUNT);
        trunks.castShadow    = true;
        trunks.receiveShadow = true;
        canopies.castShadow  = true;
        canopies.receiveShadow = true;

        const dummy = new THREE.Object3D();
        for (let i = 0; i < COUNT; i++) {
            const c = clusters[Math.floor(i / (COUNT / clusters.length)) % clusters.length]!;
            const ox = (rng() - 0.5) * c.r * 2 + c.cx;
            const oz = (rng() - 0.5) * c.r * 2 + c.cz;
            const h  = terrainHeight(ox, oz);
            const hs = 0.8 + rng() * 0.6;   // height scale variation

            dummy.position.set(ox, h + 3 * hs, oz);
            dummy.scale.set(1, hs, 1);
            dummy.rotation.set(0, rng() * Math.PI * 2, 0);
            dummy.updateMatrix();
            trunks.setMatrixAt(i, dummy.matrix);

            dummy.position.set(ox, h + (8.5 + rng() * 1.5) * hs, oz);
            dummy.scale.set(0.8 + rng() * 0.5, hs * (0.9 + rng() * 0.2), 0.8 + rng() * 0.5);
            dummy.rotation.set(0, 0, 0);
            dummy.updateMatrix();
            canopies.setMatrixAt(i, dummy.matrix);
        }
        trunks.instanceMatrix.needsUpdate   = true;
        canopies.instanceMatrix.needsUpdate = true;

        scene.add(trunks, canopies);
    }

    private _buildBuildings(scene: THREE.Scene, rng: () => number): void {
        const COUNT = 14;   // total buildings across all settlements

        const settlements = [
            { cx:   80, cz:   80, r: 80, count: 8 },   // primary village
            { cx: -500, cz:  400, r: 60, count: 6 },   // distant outpost
        ];

        const wallMat = new THREE.MeshStandardMaterial({
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

        // Unit geometries — each instance matrix applies (w, h, d) scale
        const wallGeo = new THREE.BoxGeometry(1, 1, 1);
        const roofGeo = new THREE.BoxGeometry(1, 1, 1);

        const walls = new THREE.InstancedMesh(wallGeo, wallMat, COUNT);
        const roofs = new THREE.InstancedMesh(roofGeo, roofMat, COUNT);
        walls.castShadow    = true;
        walls.receiveShadow = true;
        roofs.castShadow    = true;

        const dummy = new THREE.Object3D();
        let idx = 0;

        for (const s of settlements) {
            for (let i = 0; i < s.count && idx < COUNT; i++, idx++) {
                const bx = s.cx + (rng() - 0.5) * s.r * 2;
                const bz = s.cz + (rng() - 0.5) * s.r * 2;
                const bh = terrainHeight(bx, bz);
                const w  = 8  + rng() * 8;
                const d  = 8  + rng() * 6;
                const ht = 6  + rng() * 10;

                // Wall: position at centre, scale to actual size
                dummy.position.set(bx, bh + ht / 2, bz);
                dummy.scale.set(w, ht, d);
                dummy.rotation.y = rng() * Math.PI * 0.5;   // align to cardinal directions
                dummy.updateMatrix();
                walls.setMatrixAt(idx, dummy.matrix);

                // Roof: slight overhang
                dummy.position.set(bx, bh + ht + 1, bz);
                dummy.scale.set(w + 0.8, 2, d + 0.8);
                dummy.updateMatrix();
                roofs.setMatrixAt(idx, dummy.matrix);
            }
        }
        walls.instanceMatrix.needsUpdate = true;
        roofs.instanceMatrix.needsUpdate = true;

        scene.add(walls, roofs);
    }

    // ── Scene markers ─────────────────────────────────────────────────────────

    private _addNorthIndicator(scene: THREE.Scene): void {
        const dir   = new THREE.Vector3(0, 0, -1).normalize();
        const arrow = new THREE.ArrowHelper(dir, new THREE.Vector3(0, 1, 0), 40, 0xff4444, 10, 5);
        scene.add(arrow);
    }

    private _addOriginMarker(scene: THREE.Scene): void {
        const mat  = new THREE.LineBasicMaterial({ color: 0x444d56 });
        const pts  = [new THREE.Vector3(-20, 0.1, 0), new THREE.Vector3(20, 0.1, 0)];
        const pts2 = [new THREE.Vector3(0, 0.1, -20), new THREE.Vector3(0, 0.1,  20)];
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts),  mat));
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts2), mat));
    }
}
