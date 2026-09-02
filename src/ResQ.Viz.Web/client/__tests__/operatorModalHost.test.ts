// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it, vi } from 'vitest';

import { OperatorModalHost } from '../operator/OperatorModalHost';

function surface() {
  return { invalidate: vi.fn(), refresh: vi.fn() };
}

describe('OperatorModalHost', () => {
  it('lets a late lazy import check its generation before constructing DOM', () => {
    const host = new OperatorModalHost();
    const generation = host.begin();
    expect(host.isCurrent(generation)).toBe(true);

    host.invalidate();

    expect(host.isCurrent(generation)).toBe(false);
  });

  it('lets only the newest lazy load claim the shared modal layer', () => {
    const host = new OperatorModalHost();
    const stale = host.begin();
    const current = host.begin();
    const staleSurface = surface();
    const currentSurface = surface();

    expect(host.activate(stale, staleSurface)).toBe(false);
    expect(staleSurface.invalidate).toHaveBeenCalledOnce();
    expect(host.activate(current, currentSurface)).toBe(true);
    expect(currentSurface.invalidate).not.toHaveBeenCalled();
  });

  it('invalidates the active modal before a mode or blocking-state transition', () => {
    const host = new OperatorModalHost();
    const active = surface();
    host.activate(host.begin(), active);

    host.invalidate();

    expect(active.invalidate).toHaveBeenCalledOnce();
    expect(host.active).toBeNull();
  });

  it('retires the previous surface when another modal begins loading', () => {
    const host = new OperatorModalHost();
    const first = surface();
    host.activate(host.begin(), first);

    host.begin();

    expect(first.invalidate).toHaveBeenCalledOnce();
    expect(host.active).toBeNull();
  });

  it('releases only the surface that currently owns the layer', () => {
    const host = new OperatorModalHost();
    const current = surface();
    const other = surface();
    host.activate(host.begin(), current);

    host.release(other);
    expect(host.active).toBe(current);
    host.release(current);
    expect(host.active).toBeNull();
  });

  it('refreshes only the active modal surface', () => {
    const host = new OperatorModalHost();
    const active = surface();
    host.refresh();
    host.activate(host.begin(), active);

    host.refresh();

    expect(active.refresh).toHaveBeenCalledOnce();
  });
});
