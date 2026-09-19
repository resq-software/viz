// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it } from 'vitest';

import { buildAssetCards, DASH } from '../assets/panelCards';
import type { AssetView } from '../assets/assetView';

/**
 * The operator's asset panel is now the only surface that renders an asset's
 * power, after the editor inspector stopped repeating the nine fields both
 * panels carried. The unmetered-pack rule is safety-relevant and was previously
 * guarded only on the editor inspector, so it is re-established here against the
 * surface that actually renders it.
 */
function view(powerPercent: number | null): AssetView {
  return { powerPercent } as unknown as AssetView;
}

function remaining(v: AssetView): string | undefined {
  // descriptor and state are null: the unmetered case this guards is exactly a
  // view-only asset with no detailed power state behind it.
  for (const card of buildAssetCards(v, null, null)) {
    for (const r of card.rows) {
      if (r.label === 'Remaining') return r.value;
    }
  }
  return undefined;
}

describe('operator asset panel power', () => {
  it('renders an unmetered pack as absent, never as a flat one', () => {
    // A tether or shore supply reports null. Showing 0% would read as a dead
    // battery on a vehicle that cannot run out.
    expect(remaining(view(null))).toBe(DASH);
  });

  it('still renders a metered pack as its percentage', () => {
    expect(remaining(view(55))).toBe('55%');
  });
});
