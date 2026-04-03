// ResQ Viz - Post-processing effects pipeline
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass }     from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass }     from 'three/addons/postprocessing/OutputPass.js';

export class PostFx {
    private readonly _composer: EffectComposer;

    constructor(
        renderer: THREE.WebGLRenderer,
        scene:    THREE.Scene,
        camera:   THREE.Camera,
        width:    number,
        height:   number,
    ) {
        this._composer = new EffectComposer(renderer);
        this._composer.addPass(new RenderPass(scene, camera));
        const bloom = new UnrealBloomPass(
            new THREE.Vector2(width, height),
            0.35,   // strength
            0.6,    // radius
            0.85,   // threshold
        );
        this._composer.addPass(bloom);
        this._composer.addPass(new OutputPass());
    }

    render(): void { this._composer.render(); }
    setSize(width: number, height: number): void { this._composer.setSize(width, height); }
}
