// ResQ Viz - Ground plane terrain
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';

export class Terrain {
    private readonly _scene: THREE.Scene;

    constructor(scene: THREE.Scene) {
        this._scene = scene;
        this._build();
    }

    private _build(): void {
        const geo = new THREE.PlaneGeometry(1000, 1000, 32, 32);
        const mat = new THREE.MeshLambertMaterial({
            color: 0x2d4a1e,
            side: THREE.FrontSide,
        });
        const ground = new THREE.Mesh(geo, mat);
        ground.rotation.x = -Math.PI / 2;
        ground.receiveShadow = true;
        this._scene.add(ground);
        this._addNorthIndicator();
    }

    private _addNorthIndicator(): void {
        const dir = new THREE.ArrowHelper(
            new THREE.Vector3(0, 0, -1).normalize(),
            new THREE.Vector3(0, 1, 0),
            30,
            0xff4444,
            8,
            4,
        );
        this._scene.add(dir);
    }
}
