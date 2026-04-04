// ResQ Viz - Terrain: heightmap ground + procedural obstacles
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { PRESETS, PresetKey, TerrainPreset, _noise } from './terrainPresets';

// ── Constants ────────────────────────────────────────────────────────────────

const TERRAIN_SIZE = 4000;
const TERRAIN_SEGS = 220;

/** Minimum metres the camera stays above terrain (consumed by cameraControl). */
export const TERRAIN_MIN_ABOVE = 2.5;

// ── Active preset state ──────────────────────────────────────────────────────

let _activePreset: TerrainPreset = PRESETS['alpine'];

/** Current water level — live binding updated by setActivePreset(). */
export let WATER_LEVEL: number = _activePreset.waterLevel;

export function setActivePreset(key: PresetKey): void {
    _activePreset = PRESETS[key];
    WATER_LEVEL   = _activePreset.waterLevel;
}

/**
 * Terrain height at world position (x, z).
 * Delegates to the active preset's heightFn.
 * Called from vertex generation, camera collision, and obstacle placement.
 */
export function terrainHeight(x: number, z: number): number {
    return _activePreset.heightFn(x, z);
}

// ── GLSL helpers (colour only — injected into MeshStandardMaterial) ──────────
//   These are shader infrastructure shared by all presets.
//   The per-preset colour logic lives in preset.glslBiome.

const GLSL_VARYING = `
varying vec3 vTerrainWorld;
varying vec3 vWorldNormal;
`;

// Injected after #include <beginnormal_vertex> to capture world-space normal
const GLSL_VERT_NORMAL = `
vWorldNormal = normalize(mat3(modelMatrix) * objectNormal);
`;

// Injected after #include <begin_vertex> to capture world position
const GLSL_VERT_WORLDPOS = `
vTerrainWorld = (modelMatrix * vec4(position, 1.0)).xyz;
`;

// Value noise + 4-octave FBM for biome colouring (independent from CPU noise)
const GLSL_FRAG_NOISE = `
float _ht(vec2 p) {
    p = fract(p * vec2(127.1, 311.7));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}
float _vn(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(_ht(i),           _ht(i+vec2(1,0)), u.x),
               mix(_ht(i+vec2(0,1)), _ht(i+vec2(1,1)), u.x), u.y);
}
float _fbm(vec2 p) {
    float v=0.0; float a=0.5;
    for (int k=0;k<4;k++) { v+=a*_vn(p); p*=2.09; a*=0.47; }
    return v;
}
`;

// ── Terrain class ──────────────────────────────────────────────────────────────

export class Terrain {
    private readonly _objects: THREE.Object3D[] = [];

    constructor(scene: THREE.Scene, preset: PresetKey = 'alpine') {
        setActivePreset(preset);
        this._buildGround(scene);
        this._buildWater(scene);
        this._buildObstacles(scene);
        this._addNorthIndicator(scene);
        this._addOriginMarker(scene);
    }

    /** Track objects added to the scene so dispose() can clean them up. */
    private _sceneAdd(scene: THREE.Scene, ...objs: THREE.Object3D[]): void {
        scene.add(...objs);
        for (const o of objs) this._objects.push(o);
    }

    /** Remove all terrain objects from the scene and dispose GPU resources. */
    dispose(scene: THREE.Scene): void {
        for (const obj of this._objects) {
            scene.remove(obj);
            obj.traverse(child => {
                if ((child as THREE.Mesh).isMesh) {
                    (child as THREE.Mesh).geometry?.dispose();
                    const mat = (child as THREE.Mesh).material;
                    if (Array.isArray(mat)) mat.forEach(m => m.dispose());
                    else (mat as THREE.Material | undefined)?.dispose();
                } else if ((child as THREE.Line).isLine) {
                    (child as THREE.Line).geometry?.dispose();
                    const lmat = (child as THREE.Line).material;
                    if (Array.isArray(lmat)) lmat.forEach(m => m.dispose());
                    else (lmat as THREE.Material | undefined)?.dispose();
                }
            });
        }
        this._objects.length = 0;
    }

    // ── Ground ────────────────────────────────────────────────────────────────

    private _buildGround(scene: THREE.Scene): void {
        const geo = new THREE.PlaneGeometry(TERRAIN_SIZE, TERRAIN_SIZE, TERRAIN_SEGS, TERRAIN_SEGS);
        geo.rotateX(-Math.PI / 2);

        const pos = geo.attributes['position'] as THREE.BufferAttribute;
        for (let i = 0; i < pos.count; i++) {
            pos.setY(i, terrainHeight(pos.getX(i), pos.getZ(i)));
        }
        pos.needsUpdate = true;
        geo.computeVertexNormals();

        const mat = new THREE.MeshStandardMaterial({
            color:     0xffffff,
            roughness: 0.90,
            metalness: 0.0,
        });

        const biomeGlsl = _activePreset.glslBiome;

        mat.onBeforeCompile = (shader) => {
            shader.vertexShader = shader.vertexShader.replace(
                '#include <common>',
                `#include <common>\n${GLSL_VARYING}`,
            );
            shader.vertexShader = shader.vertexShader.replace(
                '#include <beginnormal_vertex>',
                `#include <beginnormal_vertex>\n${GLSL_VERT_NORMAL}`,
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
                biomeGlsl,
            );
        };
        mat.customProgramCacheKey = () => _activePreset.cacheKey;

        const mesh = new THREE.Mesh(geo, mat);
        mesh.receiveShadow = true;
        this._sceneAdd(scene, mesh);
    }

    // ── Water ─────────────────────────────────────────────────────────────────

    private _buildWater(scene: THREE.Scene): void {
        const geo = new THREE.PlaneGeometry(TERRAIN_SIZE + 600, TERRAIN_SIZE + 600, 1, 1);
        geo.rotateX(-Math.PI / 2);

        const mat = new THREE.MeshStandardMaterial({
            color:       0x1a5f8c,
            roughness:   0.06,
            metalness:   0.18,
            transparent: true,
            opacity:     0.80,
        });

        const mesh = new THREE.Mesh(geo, mat);
        mesh.position.y = _activePreset.waterLevel;
        mesh.renderOrder = 0;
        this._sceneAdd(scene, mesh);
    }

    // ── Obstacles ─────────────────────────────────────────────────────────────

    private _buildObstacles(scene: THREE.Scene): void {
        // Deterministic LCG — same layout on every load for the same preset
        let seed = 42;
        const rng = (): number => {
            seed = (seed * 1_664_525 + 1_013_904_223) & 0xffff_ffff;
            return (seed >>> 0) / 0xffff_ffff;
        };
        this._buildTrees(scene, rng);
        this._buildRocks(scene, rng);
        this._buildBuildings(scene, rng);
    }

    // ── Trees ─────────────────────────────────────────────────────────────────

    private _buildTrees(scene: THREE.Scene, rng: () => number): void {
        const { pineCount: PINE_N, decidCount: DECID_N, minTreeH, maxTreeH, waterLevel } = _activePreset;
        if (PINE_N + DECID_N === 0) return;

        const trunkGeo = new THREE.CylinderGeometry(0.32, 0.55, 5.5, 7);
        const pineGeo  = new THREE.ConeGeometry(3.0, 8.0, 8);
        const decidGeo = new THREE.SphereGeometry(3.8, 8, 6);

        const trunkMatP = new THREE.MeshStandardMaterial({ color: 0x3a2a1a, roughness: 0.97, metalness: 0 });
        const trunkMatD = new THREE.MeshStandardMaterial({ color: 0x4a3728, roughness: 0.95, metalness: 0 });
        const pineMat   = new THREE.MeshStandardMaterial({ color: 0x1a4422, roughness: 0.92, metalness: 0 });
        const decidMat  = new THREE.MeshStandardMaterial({ color: 0x2d5a1b, roughness: 0.90, metalness: 0 });

        const pineTrunks  = new THREE.InstancedMesh(trunkGeo, trunkMatP, PINE_N);
        const pineCan     = new THREE.InstancedMesh(pineGeo,  pineMat,   PINE_N);
        const decidTrunks = DECID_N > 0 ? new THREE.InstancedMesh(trunkGeo, trunkMatD, DECID_N) : null;
        const decidCan    = DECID_N > 0 ? new THREE.InstancedMesh(decidGeo, decidMat,  DECID_N) : null;

        for (const m of [pineTrunks, pineCan, decidTrunks, decidCan]) {
            if (m) { m.castShadow = true; m.receiveShadow = true; }
        }

        // Forest density noise — creates organic patches (~250 m across)
        const forestDensity = (x: number, z: number): number =>
            _noise(x * 0.0035 + 3.7, z * 0.0035 + 1.1) * 0.60 +
            _noise(x * 0.009  + 7.2, z * 0.009  + 4.3) * 0.40;

        const dummy = new THREE.Object3D();
        let pi = 0, di = 0;
        const attempts = Math.max(10_000, (PINE_N + DECID_N) * 40);

        for (let attempt = 0; attempt < attempts && (pi < PINE_N || di < DECID_N); attempt++) {
            const ox = (rng() - 0.5) * TERRAIN_SIZE * 0.93;
            const oz = (rng() - 0.5) * TERRAIN_SIZE * 0.93;
            const h  = terrainHeight(ox, oz);

            if (h < waterLevel + 2.5) continue;
            if (h < minTreeH || h > maxTreeH) continue;

            const fd = forestDensity(ox, oz);
            if (rng() > fd + 0.08) continue;

            const wantPine  = (h > 30 || rng() < 0.25) && pi < PINE_N;
            const wantDecid = !wantPine && DECID_N > 0 && di < DECID_N;
            if (!wantPine && !wantDecid) continue;

            const hs = 0.75 + rng() * 0.65;
            const rs = 0.80 + rng() * 0.45;

            dummy.position.set(ox, h + 2.75 * hs, oz);
            dummy.scale.set(1, hs, 1);
            dummy.rotation.set(0, rng() * Math.PI * 2, 0);
            dummy.updateMatrix();

            if (wantPine) {
                pineTrunks.setMatrixAt(pi, dummy.matrix);
                dummy.position.set(ox, h + (8.0 + rng() * 2.0) * hs, oz);
                dummy.scale.set(rs * 0.85, hs * 1.15, rs * 0.85);
                dummy.updateMatrix();
                pineCan.setMatrixAt(pi, dummy.matrix);
                pi++;
            } else if (decidTrunks && decidCan) {
                decidTrunks.setMatrixAt(di, dummy.matrix);
                dummy.position.set(ox, h + (7.5 + rng() * 1.8) * hs, oz);
                dummy.scale.set(rs, hs * 0.88, rs);
                dummy.updateMatrix();
                decidCan.setMatrixAt(di, dummy.matrix);
                di++;
            }
        }

        dummy.scale.setScalar(0); dummy.updateMatrix();
        for (let i = pi; i < PINE_N; i++) {
            pineTrunks.setMatrixAt(i, dummy.matrix);
            pineCan.setMatrixAt(i, dummy.matrix);
        }
        if (decidTrunks && decidCan) {
            for (let i = di; i < DECID_N; i++) {
                decidTrunks.setMatrixAt(i, dummy.matrix);
                decidCan.setMatrixAt(i, dummy.matrix);
            }
        }

        pineTrunks.instanceMatrix.needsUpdate = true;
        pineCan.instanceMatrix.needsUpdate    = true;
        if (decidTrunks) decidTrunks.instanceMatrix.needsUpdate = true;
        if (decidCan)    decidCan.instanceMatrix.needsUpdate    = true;

        const toAdd: THREE.Object3D[] = [pineTrunks, pineCan];
        if (decidTrunks) toAdd.push(decidTrunks);
        if (decidCan)    toAdd.push(decidCan);
        this._sceneAdd(scene, ...toAdd);
    }

    // ── Rocky outcrops ────────────────────────────────────────────────────────

    private _buildRocks(scene: THREE.Scene, rng: () => number): void {
        const { rockCount: ROCK_N, waterLevel } = _activePreset;
        if (ROCK_N === 0) return;

        const rockGeo = new THREE.IcosahedronGeometry(1, 1);
        const rockMat = new THREE.MeshStandardMaterial({
            color:     0x5c5650,
            roughness: 0.93,
            metalness: 0.02,
        });

        const rocks = new THREE.InstancedMesh(rockGeo, rockMat, ROCK_N);
        rocks.castShadow    = true;
        rocks.receiveShadow = true;

        const dummy = new THREE.Object3D();
        let idx = 0;
        // Rocks prefer elevated terrain — threshold relative to this preset's water level
        const rockMinH = waterLevel + 15;
        const attempts = Math.max(4_000, ROCK_N * 20);

        for (let attempt = 0; attempt < attempts && idx < ROCK_N; attempt++) {
            const cx = (rng() - 0.5) * TERRAIN_SIZE * 0.88;
            const cz = (rng() - 0.5) * TERRAIN_SIZE * 0.88;
            const ch = terrainHeight(cx, cz);

            if (ch < rockMinH) continue;

            // Cluster: 1–4 boulders per outcrop
            const clusterN = 1 + Math.floor(rng() * 4);
            for (let k = 0; k < clusterN && idx < ROCK_N; k++, idx++) {
                const ox = cx + (rng() - 0.5) * 14;
                const oz = cz + (rng() - 0.5) * 14;
                const oh = terrainHeight(ox, oz);

                const w  = 1.6 + rng() * 4.0;
                const ht = 1.0 + rng() * 2.8;
                const d  = 1.4 + rng() * 3.5;

                dummy.position.set(ox, oh + ht * 0.45, oz);
                dummy.scale.set(w, ht, d);
                dummy.rotation.set(
                    (rng() - 0.5) * 0.5,
                    rng() * Math.PI * 2,
                    (rng() - 0.5) * 0.5,
                );
                dummy.updateMatrix();
                rocks.setMatrixAt(idx, dummy.matrix);
            }
        }

        dummy.scale.setScalar(0); dummy.updateMatrix();
        for (; idx < ROCK_N; idx++) rocks.setMatrixAt(idx, dummy.matrix);

        rocks.instanceMatrix.needsUpdate = true;
        this._sceneAdd(scene, rocks);
    }

    // ── Buildings ─────────────────────────────────────────────────────────────

    private _buildBuildings(scene: THREE.Scene, rng: () => number): void {
        const settlements = _activePreset.settlements;
        if (settlements.length === 0) return;

        const COUNT = settlements.reduce((s, x) => s + x.count, 0);

        const wallMat = new THREE.MeshStandardMaterial({
            color: 0x2e2b27, roughness: 0.84, metalness: 0.0, envMapIntensity: 0.3,
        });
        const roofMat = new THREE.MeshStandardMaterial({
            color: 0x3d1e14, roughness: 0.82, metalness: 0,   envMapIntensity: 0.3,
        });

        const wallGeo = new THREE.BoxGeometry(1, 1, 1);
        const roofGeo = new THREE.ConeGeometry(1, 1, 4);

        const walls = new THREE.InstancedMesh(wallGeo, wallMat, COUNT);
        const roofs = new THREE.InstancedMesh(roofGeo, roofMat, COUNT);
        walls.castShadow    = true;
        walls.receiveShadow = true;
        roofs.castShadow    = true;

        const dummy = new THREE.Object3D();
        let idx = 0;

        for (const s of settlements) {
            for (let i = 0; i < s.count && idx < COUNT; i++, idx++) {
                const bx  = s.cx + (rng() - 0.5) * s.r * 2;
                const bz  = s.cz + (rng() - 0.5) * s.r * 2;
                const bh  = terrainHeight(bx, bz);
                const w   = 8  + rng() * 8;
                const d   = 8  + rng() * 6;
                const ht  = 5  + rng() * 9;
                const rot = (rng() < 0.5 ? 0 : Math.PI * 0.5) + rng() * 0.15;

                dummy.position.set(bx, bh + ht / 2, bz);
                dummy.scale.set(w, ht, d);
                dummy.rotation.set(0, rot, 0);
                dummy.updateMatrix();
                walls.setMatrixAt(idx, dummy.matrix);

                const roofH = 2.5 + rng() * 2.0;
                const hw    = Math.max(w, d) * 0.72;
                dummy.position.set(bx, bh + ht + roofH * 0.5, bz);
                dummy.scale.set(hw, roofH, hw);
                dummy.rotation.set(0, rot + Math.PI * 0.25, 0);
                dummy.updateMatrix();
                roofs.setMatrixAt(idx, dummy.matrix);
            }
        }

        walls.instanceMatrix.needsUpdate = true;
        roofs.instanceMatrix.needsUpdate = true;
        this._sceneAdd(scene, walls, roofs);
    }

    // ── Scene markers ──────────────────────────────────────────────────────────

    private _addNorthIndicator(scene: THREE.Scene): void {
        const h    = terrainHeight(0, 0);
        const dir  = new THREE.Vector3(0, 0, -1);
        const orig = new THREE.Vector3(0, h + 1.5, 0);
        this._sceneAdd(scene, new THREE.ArrowHelper(dir, orig, 55, 0xff4444, 14, 6));
    }

    private _addOriginMarker(scene: THREE.Scene): void {
        const y   = terrainHeight(0, 0) + 0.2;
        const mat = new THREE.LineBasicMaterial({ color: 0x444d56 });
        const h   = [new THREE.Vector3(-25, y, 0), new THREE.Vector3(25, y, 0)];
        const v   = [new THREE.Vector3(0, y, -25), new THREE.Vector3(0, y, 25)];
        this._sceneAdd(
            scene,
            new THREE.Line(new THREE.BufferGeometry().setFromPoints(h), mat),
            new THREE.Line(new THREE.BufferGeometry().setFromPoints(v), mat),
        );
    }
}
