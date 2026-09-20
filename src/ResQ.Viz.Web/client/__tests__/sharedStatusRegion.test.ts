// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0
import { describe, expect, it } from 'vitest';
import { createSharedStatusRegion } from '../a11y/sharedStatusRegion';

/** A region plus a clock the test drives, so no test waits out a real hold. */
function harness(holdMs = 6000, periodicMs = 8000) {
  const element = document.createElement('div');
  let clock = 1000;
  const region = createSharedStatusRegion({
    element, holdMs, periodicMs, now: () => clock,
  });
  return {
    region,
    text: (): string => element.textContent ?? '',
    advance: (ms: number): void => { clock += ms; },
  };
}

describe('shared status region', () => {
  it('lets the periodic summary speak when nothing holds the floor', () => {
    const h = harness();
    expect(h.region.periodic(3, () => '3 drones active.')).toBe(true);
    expect(h.text()).toBe('3 drones active.');
  });

  it('throttles an unchanged signature but not a changed one', () => {
    const h = harness();
    h.region.periodic(3, () => 'first');
    h.advance(100);
    expect(h.region.periodic(3, () => 'second'), 'same signature, inside floor').toBe(false);
    expect(h.text()).toBe('first');

    expect(h.region.periodic(4, () => 'third'), 'signature changed').toBe(true);
    expect(h.text()).toBe('third');
  });

  it('does not compose text it would discard', () => {
    const h = harness();
    h.region.periodic(3, () => 'first');
    h.advance(100);
    let composed = 0;
    h.region.periodic(3, () => { composed++; return 'x'; });
    expect(composed, 'the lazy callback stayed lazy').toBe(0);
  });

  // The defect this module exists for. An emergency stop changes fleet state,
  // which changes the signature, which is exactly when the summary would fire.
  it('keeps a safety announcement from being overwritten by the summary', () => {
    const h = harness();
    h.region.periodic(3, () => '3 drones active.');

    h.region.priority('Emergency stop accepted for drone-2.');
    expect(h.text()).toBe('Emergency stop accepted for drone-2.');

    h.advance(16);
    expect(h.region.periodic(2, () => '2 drones active.'), 'suppressed under hold').toBe(false);
    expect(h.text(), 'the confirmation survived').toBe('Emergency stop accepted for drone-2.');
  });

  it('returns the floor to the summary once the hold lapses', () => {
    const h = harness(6000);
    h.region.priority('Emergency stop accepted for drone-2.');
    h.advance(5999);
    expect(h.region.periodic(2, () => 'later')).toBe(false);
    h.advance(2);
    expect(h.region.periodic(2, () => 'later'), 'hold lapsed').toBe(true);
    expect(h.text()).toBe('later');
  });

  // Without clearing the signature, an unchanged fleet would leave the stale
  // emergency-stop line up for a further `periodicMs` after the hold lapsed.
  it('does not leave the region stale when the fleet has not changed', () => {
    const h = harness(6000, 8000);
    h.region.periodic(3, () => '3 drones active.');
    h.region.priority('Emergency stop accepted for drone-2.');
    h.advance(6001);
    expect(h.region.periodic(3, () => '3 drones active.'), 'same signature as before the stop').toBe(true);
    expect(h.text()).toBe('3 drones active.');
  });

  it('speaks immediately after invalidate, even inside the floor', () => {
    const h = harness();
    h.region.periodic(3, () => 'v2 wording');
    h.advance(100);
    h.region.invalidate();
    expect(h.region.periodic(3, () => 'v1 wording'), 'wording changed, signature did not').toBe(true);
    expect(h.text()).toBe('v1 wording');
  });

  it('is inert without an element rather than throwing', () => {
    const region = createSharedStatusRegion({
      element: null, holdMs: 10, periodicMs: 10, now: () => 0,
    });
    expect(region.periodic(1, () => 'x')).toBe(false);
    expect(() => { region.priority('x'); region.invalidate(); }).not.toThrow();
  });
});
