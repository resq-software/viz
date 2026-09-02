// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { LoadingOverlay } from '../loadingOverlay';

function overlayElement(): HTMLElement {
  const element = document.querySelector<HTMLElement>('.loading-overlay');
  if (!element) throw new Error('loading overlay was not mounted');
  return element;
}

beforeEach(() => {
  vi.useFakeTimers();
  document.body.innerHTML = '';
});

afterEach(() => {
  vi.clearAllTimers();
  vi.useRealTimers();
});

describe('LoadingOverlay startup status', () => {
  it('shows a persistent accessible startup error with recovery guidance', () => {
    const overlay = new LoadingOverlay();

    overlay.setStartupStatus('error');

    const element = overlayElement();
    expect(element.classList.contains('visible')).toBe(true);
    expect(element.classList.contains('disconnected')).toBe(true);
    expect(element.getAttribute('role')).toBe('alert');
    expect(element.getAttribute('aria-live')).toBe('assertive');
    expect(element.querySelector('.loading-title')?.textContent)
      .toBe('Simulation link unavailable');
    expect(element.querySelector('.loading-phase')?.textContent)
      .toBe('Retrying automatically…');
    expect(element.querySelector('.loading-sub')?.textContent)
      .toBe('Check the simulation host and network connection, or reload this page.');
    expect(element.querySelector<HTMLButtonElement>('.loading-retry')?.textContent).toBe('Reload');
  });

  it('restores connecting presentation and still hides on a late frame', () => {
    const overlay = new LoadingOverlay();
    overlay.setStartupStatus('error');

    overlay.setStartupStatus('connecting');

    const element = overlayElement();
    expect(element.classList.contains('visible')).toBe(true);
    expect(element.classList.contains('connecting')).toBe(true);
    expect(element.classList.contains('disconnected')).toBe(false);
    expect(element.getAttribute('role')).toBe('status');
    expect(element.getAttribute('aria-live')).toBe('polite');
    expect(element.querySelector('.loading-title')?.textContent).toBe('ResQ Viz');
    expect(element.querySelector('.loading-phase')?.textContent).toBe('Initializing geometry cache');
    expect(element.querySelector('.loading-sub')?.textContent).toBe('Live coordination');

    overlay.onFrame();
    expect(element.classList.contains('visible')).toBe(false);
  });

  it('retains distinct lost-connection wording after a previously live frame', async () => {
    const overlay = new LoadingOverlay();
    overlay.onFrame();

    overlay.onDisconnected();
    await vi.advanceTimersByTimeAsync(5_000);

    const element = overlayElement();
    expect(element.querySelector('.loading-title')?.textContent).toBe('Connection lost');
    expect(element.querySelector('.loading-sub')?.textContent)
      .toBe('Check the host and try reloading if it persists.');
  });

  it('does not cover the last good picture with cold-start presentation', () => {
    const overlay = new LoadingOverlay();
    overlay.onFrame();
    const element = overlayElement();

    overlay.setStartupStatus('error');
    expect(element.classList.contains('visible')).toBe(false);

    overlay.setStartupStatus('connecting');
    expect(element.classList.contains('visible')).toBe(false);
  });
});
