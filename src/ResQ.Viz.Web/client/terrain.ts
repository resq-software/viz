// ResQ Viz - Ground plane terrain
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

export class Terrain {
    constructor(scene: THREE.Scene) {
        this._buildGround(scene);
        this._addNorthIndicator(scene);
        this._addOriginMarker(scene);
    }

    private _buildGround(scene: THREE.Scene): void {
        const geo = new THREE.PlaneGeometry(2000, 2000, 1, 1);
        const mat = new THREE.MeshLambertMaterial({
            color:  0x1a2d12,
            side:   THREE.FrontSide,
        });
        const ground = new THREE.Mesh(geo, mat);
        ground.rotation.x = -Math.PI / 2;
        ground.receiveShadow = true;
        scene.add(ground);
    }

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
