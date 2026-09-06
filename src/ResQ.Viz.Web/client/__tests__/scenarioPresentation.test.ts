// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it, vi } from 'vitest';

import {
  applyScenarioEnvironment,
  environmentFor,
} from '../scenarioEnvironments';
import {
  humaniseScenarioName,
  scenarioPresentation,
} from '../operator/scenarioPresentation';

const PROTOTYPE_NAMES = Object.getOwnPropertyNames(Object.prototype);

describe('hostile scenario names', () => {
  it.each(PROTOTYPE_NAMES)('presents inherited Object key %s through the Other fallback', name => {
    expect(() => scenarioPresentation(name)).not.toThrow();
    expect(scenarioPresentation(name)).toEqual({
      displayName: humaniseScenarioName(name),
      category: 'Other',
      purpose: 'Configured scenario',
      environment: null,
    });
  });

  it.each(PROTOTYPE_NAMES)('does not resolve or apply inherited Object key %s as an environment', name => {
    const applyScene = vi.fn();
    const switchPreset = vi.fn();
    const setCamera = vi.fn();
    const isTerrainOverridden = vi.fn(() => false);

    expect(environmentFor(name)).toBeNull();
    expect(applyScenarioEnvironment({
      applyScene,
      switchPreset,
      setCamera,
      isTerrainOverridden,
    }, name)).toBe(false);
    expect(applyScene).not.toHaveBeenCalled();
    expect(switchPreset).not.toHaveBeenCalled();
    expect(setCamera).not.toHaveBeenCalled();
    expect(isTerrainOverridden).not.toHaveBeenCalled();
  });
});
