// ResQ Viz - Terrain: heightmap ground + procedural obstacles
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

/** Terrain height at world position (x, z). Max amplitude ≈ 9m. */
export function terrainHeight(x: number, z: number): number {
    return Math.sin(x * 0.018)          * Math.cos(z * 0.014)          * 5.0
         + Math.sin(x * 0.041 + 1.7)   * Math.cos(z * 0.037 + 0.9)   * 2.5
         + Math.sin(x * 0.089 + 3.2)   * Math.cos(z * 0.076 + 2.1)   * 1.2
         + Math.sin(x * 0.162 + 0.4)   * Math.cos(z * 0.154 + 1.5)   * 0.5;
}

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

        // Displace vertices
        const pos = geo.attributes.position as THREE.BufferAttribute;
        for (let i = 0; i < pos.count; i++) {
            const x = pos.getX(i);
            const z = pos.getZ(i);
            pos.setY(i, terrainHeight(x, z));
        }
        pos.needsUpdate = true;
        geo.computeVertexNormals();

        // Add vertex colours: darker in valleys, lighter on ridges
        const colors = new Float32Array(pos.count * 3);
        const low  = new THREE.Color(0x1a2d12);  // dark valley green
        const high = new THREE.Color(0x2e4a1e);  // lighter ridge green
        const tmp  = new THREE.Color();
        for (let i = 0; i < pos.count; i++) {
            const h = pos.getY(i);
            // h range roughly -9 to +9, normalise 0–1
            const t = THREE.MathUtils.clamp((h + 9) / 18, 0, 1);
            tmp.lerpColors(low, high, t);
            colors[i * 3]     = tmp.r;
            colors[i * 3 + 1] = tmp.g;
            colors[i * 3 + 2] = tmp.b;
        }
        geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));

        const mat = new THREE.MeshStandardMaterial({
            vertexColors: true,
            roughness:    0.92,
            metalness:    0.0,
        });

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
            // Place trees in two clusters to look natural
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
            color:     0x8a8070,
            roughness: 0.8,
            metalness: 0.05,
        });
        const roofMat = new THREE.MeshStandardMaterial({
            color:     0x6b3a2a,
            roughness: 0.85,
            metalness: 0,
        });

        const settlement = { cx: 80, cz: 80 };
        for (let i = 0; i < 6; i++) {
            const bx = settlement.cx + (rng() - 0.5) * 80;
            const bz = settlement.cz + (rng() - 0.5) * 80;
            const bh = terrainHeight(bx, bz);
            const w  = 8  + rng() * 8;
            const d  = 8  + rng() * 6;
            const ht = 6  + rng() * 10;

            const wallGeo = new THREE.BoxGeometry(w, ht, d);
            const wall    = new THREE.Mesh(wallGeo, buildingMat);
            wall.position.set(bx, bh + ht / 2, bz);
            wall.castShadow    = true;
            wall.receiveShadow = true;

            // Simple pitched roof (scaled box)
            const roofGeo = new THREE.BoxGeometry(w + 0.5, 2, d + 0.5);
            const roof    = new THREE.Mesh(roofGeo, roofMat);
            roof.position.set(bx, bh + ht + 1, bz);
            roof.castShadow = true;

            scene.add(wall, roof);
        }
    }

    // ── Scene markers ─────────────────────────────────────────────────────────

    private _addNorthIndicator(scene: THREE.Scene): void {
        // Arrow pointing North (+Z in this coord system = South, so -Z = North)
        const dir = new THREE.Vector3(0, 0, -1).normalize();
        const arrow = new THREE.ArrowHelper(dir, new THREE.Vector3(0, 1, 0), 40, 0xff4444, 10, 5);
        scene.add(arrow);
    }

    private _addOriginMarker(scene: THREE.Scene): void {
        // Small cross at origin for reference
        const mat = new THREE.LineBasicMaterial({ color: 0x444d56 });
        const pts = [
            new THREE.Vector3(-20, 0.1, 0),
            new THREE.Vector3( 20, 0.1, 0),
        ];
        const pts2 = [
            new THREE.Vector3(0, 0.1, -20),
            new THREE.Vector3(0, 0.1,  20),
        ];
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat));
        scene.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts2), mat));
    }
}
