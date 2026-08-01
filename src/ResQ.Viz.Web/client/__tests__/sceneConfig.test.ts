// SPDX-License-Identifier: Apache-2.0
//
// Unit tests for the scene-descriptor (de)serialization. The SceneConfigPanel
// DOM/export/import is covered by E2E; this pins the validation + round-trip
// that decides whether an imported file is accepted and what it means.

import { describe, expect, it } from 'vitest';

import {
    parseSceneConfig,
    serializeSceneConfig,
    SCENE_CONFIG_VERSION,
    type SceneConfig,
} from '../editor/sceneConfig';

describe('parseSceneConfig', () => {
    it('accepts a well-formed descriptor', () => {
        const cfg = parseSceneConfig({ version: 1, terrain: 'alpine', scenario: 'swarm-5' });
        expect(cfg).toEqual({ version: 1, terrain: 'alpine', scenario: 'swarm-5' });
    });

    it('normalises a missing/undefined scenario to null', () => {
        expect(parseSceneConfig({ version: 1, terrain: 'coastal' })?.scenario).toBeNull();
        expect(parseSceneConfig({ version: 1, terrain: 'coastal', scenario: null })?.scenario).toBeNull();
    });

    it('rejects non-objects', () => {
        for (const bad of [null, undefined, 42, 'x', []]) {
            expect(parseSceneConfig(bad)).toBeNull();
        }
    });

    it('rejects missing/typewrong required fields', () => {
        expect(parseSceneConfig({ terrain: 'alpine' })).toBeNull(); // no version
        expect(parseSceneConfig({ version: 1 })).toBeNull(); // no terrain
        expect(parseSceneConfig({ version: '1', terrain: 'alpine' })).toBeNull(); // version not number
        expect(parseSceneConfig({ version: 1, terrain: 5 })).toBeNull(); // terrain not string
        expect(parseSceneConfig({ version: 1, terrain: 'alpine', scenario: 7 })).toBeNull(); // scenario wrong type
    });
});

describe('serializeSceneConfig', () => {
    it('round-trips through parse', () => {
        const cfg: SceneConfig = { version: SCENE_CONFIG_VERSION, terrain: 'canyon', scenario: 'sar' };
        const json = serializeSceneConfig(cfg);
        expect(parseSceneConfig(JSON.parse(json))).toEqual(cfg);
    });

    it('emits pretty (indented) JSON', () => {
        const json = serializeSceneConfig({ version: 1, terrain: 'alpine', scenario: null });
        expect(json).toContain('\n');
    });
});
