// SPDX-License-Identifier: Apache-2.0
//
// Hazard zone geometry has terrain heights baked into its vertices, so it has to
// be rebuilt when the terrain moves.
//
// Conforming the zones to the ground introduced this: before, they sat at a fixed
// Y and were uniformly wrong; now they are right until a preset switch or a
// heightmap install replaces the surface beneath them, at which point every
// vertex holds the height of a world that is no longer drawn. `_updateHazards`
// keeps entries whose ids are still present, so nothing would have rebuilt them.

import * as THREE from 'three';
import { afterEach, describe, expect, it } from 'vitest';

import { EffectsManager } from '../effects';
import { setHeightmapOverride } from '../terrain';
import type { VizFrame } from '../types';

/** A frame carrying one hazard, well away from the origin so terrain varies. */
function frameWithHazard(): VizFrame {
    return {
        drones: [],
        hazards: [{ id: 'hz-1', type: 'flood', center: [120, 0, -80], radius: 60 }],
        detections: [],
    } as unknown as VizFrame;
}

/**
 * Mean Y of the zone FILL's vertices — the thing terrain moves.
 *
 * Narrowed to the fill specifically, by its 4-component colour attribute (the
 * centre-to-rim alpha ramp), which nothing else in the scene has. Averaging
 * every mesh instead picks up the 32 pooled survivor-marker spheres the
 * constructor allocates, and they swamp the signal — the first version of this
 * test read 78.9 either side of a change that had in fact worked.
 */
function meanY(scene: THREE.Scene): number {
    let sum = 0;
    let n = 0;
    scene.traverse(obj => {
        const geo = (obj as THREE.Mesh).geometry;
        const col = geo?.getAttribute?.('color') as THREE.BufferAttribute | undefined;
        if (!col || col.itemSize !== 4) return;
        const pos = geo.getAttribute('position') as THREE.BufferAttribute;
        for (let i = 0; i < pos.count; i++) {
            sum += pos.getY(i);
            n++;
        }
    });
    return n === 0 ? Number.NaN : sum / n;
}

afterEach(() => {
    // Module-level terrain state is shared; leaving an override installed would
    // silently change every later test's ground.
    setHeightmapOverride(null);
});

describe('hazard zones and terrain changes', () => {
    it('rebuilds zone geometry when a heightmap is installed', () => {
        const scene = new THREE.Scene();
        const fx = new EffectsManager(scene);

        fx.update(frameWithHazard());
        const before = meanY(scene);
        expect(Number.isFinite(before)).toBe(true);

        // A surface nothing procedural could produce, so the assertion cannot
        // pass by coincidence.
        setHeightmapOverride({ sample: () => 500 } as never);
        fx.update(frameWithHazard());

        const after = meanY(scene);
        expect(after).toBeGreaterThan(400);
        expect(after).not.toBeCloseTo(before, 1);
    });

    it('follows the terrain back down when the override is cleared', () => {
        // The complement. Rebuilding only on the way up would leave zones
        // stranded at the uploaded DEM's heights for the rest of the session.
        const scene = new THREE.Scene();
        const fx = new EffectsManager(scene);

        setHeightmapOverride({ sample: () => 500 } as never);
        fx.update(frameWithHazard());
        expect(meanY(scene)).toBeGreaterThan(400);

        setHeightmapOverride(null);
        fx.update(frameWithHazard());
        expect(meanY(scene)).toBeLessThan(400);
    });

    it('keeps the zone present across the rebuild', () => {
        // Dropping the entries is how the rebuild works, so the thing worth
        // pinning is that they come back rather than vanishing on a preset switch.
        const scene = new THREE.Scene();
        const fx = new EffectsManager(scene);

        fx.update(frameWithHazard());
        const meshesBefore = scene.children.length;

        setHeightmapOverride({ sample: () => 250 } as never);
        fx.update(frameWithHazard());

        expect(scene.children.length).toBe(meshesBefore);
    });
});
