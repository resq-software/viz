// ResQ Viz - Post-processing effects pipeline
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass }     from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { SAOPass }        from 'three/addons/postprocessing/SAOPass.js';
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
        const sao = new SAOPass(scene, camera);
        sao.params.saoBias          = 0.5;
        sao.params.saoIntensity     = 0.008;
        sao.params.saoScale         = 0.8;
        sao.params.saoKernelRadius  = 20;
        sao.params.saoMinResolution = 0;
        sao.params.saoBlur          = true;
        sao.params.saoBlurRadius    = 8;
        sao.params.saoBlurStdDev    = 4;
        this._composer.addPass(sao);
        const bloom = new UnrealBloomPass(
            new THREE.Vector2(width, height),
            0.18,   // strength
            0.25,   // radius
            0.92,   // threshold
        );
        this._composer.addPass(bloom);
        this._composer.addPass(new OutputPass());
    }

    render(): void { this._composer.render(); }
    setSize(width: number, height: number): void { this._composer.setSize(width, height); }
}
