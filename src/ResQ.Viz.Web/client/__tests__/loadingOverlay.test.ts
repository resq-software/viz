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
  it('starts visibly active without exposing the hidden Reload action', () => {
    new LoadingOverlay();

    const element = overlayElement();
    const retry = element.querySelector<HTMLButtonElement>('.loading-retry');
    expect(element.hidden).toBe(false);
    expect(element.hasAttribute('inert')).toBe(false);
    expect(element.getAttribute('aria-hidden')).toBe('false');
    expect(element.getAttribute('role')).toBe('group');
    expect(element.getAttribute('aria-live')).toBe('off');
    expect(retry?.hidden).toBe(true);
    expect(retry?.disabled).toBe(true);
    expect(retry?.tabIndex).toBe(-1);
    retry?.focus();
    expect(document.activeElement).not.toBe(retry);
  });

  it('shows a persistent accessible startup error with recovery guidance', () => {
    const overlay = new LoadingOverlay();

    overlay.setStartupStatus('error');

    const element = overlayElement();
    const retry = element.querySelector<HTMLButtonElement>('.loading-retry');
    expect(element.classList.contains('visible')).toBe(true);
    expect(element.classList.contains('disconnected')).toBe(true);
    expect(element.hidden).toBe(false);
    expect(element.hasAttribute('inert')).toBe(false);
    expect(element.getAttribute('aria-hidden')).toBe('false');
    expect(element.getAttribute('role')).toBe('group');
    expect(element.getAttribute('aria-live')).toBe('off');
    expect(element.querySelector('.loading-title')?.textContent)
      .toBe('Simulation link unavailable');
    expect(element.querySelector('.loading-phase')?.textContent)
      .toBe('Retrying automatically…');
    expect(element.querySelector('.loading-sub')?.textContent)
      .toBe('Check the simulation host and network connection, or reload this page.');
    expect(retry?.textContent).toBe('Reload');
    expect(retry?.hidden).toBe(false);
    expect(retry?.disabled).toBe(false);
    expect(retry?.tabIndex).toBe(0);
    retry?.focus();
    expect(document.activeElement).toBe(retry);
  });

  it('restores connecting presentation and still hides on a late frame', () => {
    const overlay = new LoadingOverlay();
    overlay.setStartupStatus('error');

    overlay.setStartupStatus('connecting');

    const element = overlayElement();
    expect(element.classList.contains('visible')).toBe(true);
    expect(element.classList.contains('connecting')).toBe(true);
    expect(element.classList.contains('disconnected')).toBe(false);
    expect(element.hidden).toBe(false);
    expect(element.hasAttribute('inert')).toBe(false);
    expect(element.getAttribute('aria-hidden')).toBe('false');
    expect(element.getAttribute('role')).toBe('group');
    expect(element.getAttribute('aria-live')).toBe('off');
    expect(element.querySelector('.loading-title')?.textContent).toBe('ResQ Viz');
    expect(element.querySelector('.loading-phase')?.textContent).toBe('Initializing geometry cache');
    expect(element.querySelector('.loading-sub')?.textContent).toBe('Live coordination');
    const retry = element.querySelector<HTMLButtonElement>('.loading-retry');
    expect(retry?.disabled).toBe(true);
    expect(document.activeElement).not.toBe(retry);

    overlay.onFrame();
    expect(element.classList.contains('visible')).toBe(false);
    expect(element.hidden).toBe(true);
    expect(element.hasAttribute('inert')).toBe(true);
    expect(element.getAttribute('aria-hidden')).toBe('true');
    expect(retry?.disabled).toBe(true);
    expect(retry?.tabIndex).toBe(-1);
  });

  it('retains distinct lost-connection wording after a previously live frame', async () => {
    const overlay = new LoadingOverlay();
    overlay.onFrame();

    overlay.onDisconnected();
    await vi.advanceTimersByTimeAsync(5_000);

    const element = overlayElement();
    const retry = element.querySelector<HTMLButtonElement>('.loading-retry');
    expect(element.hidden).toBe(false);
    expect(element.hasAttribute('inert')).toBe(false);
    expect(element.getAttribute('aria-hidden')).toBe('false');
    expect(element.getAttribute('role')).toBe('alert');
    expect(element.getAttribute('aria-live')).toBe('assertive');
    expect(retry?.disabled).toBe(false);
    expect(retry?.tabIndex).toBe(0);
    expect(element.querySelector('.loading-title')?.textContent).toBe('Connection lost');
    expect(element.querySelector('.loading-sub')?.textContent)
      .toBe('Check the host and try reloading if it persists.');
  });

  it('clears an established lost-connection card on explicit reconnect success before a frame', async () => {
    const overlay = new LoadingOverlay();
    overlay.onFrame();
    overlay.onDisconnected();
    await vi.advanceTimersByTimeAsync(5_000);
    const element = overlayElement();
    const retry = element.querySelector<HTMLButtonElement>('.loading-retry');
    expect(element.classList.contains('disconnected')).toBe(true);

    overlay.onReconnected();

    expect(element.classList.contains('disconnected')).toBe(false);
    expect(element.hidden).toBe(true);
    expect(element.hasAttribute('inert')).toBe(true);
    expect(element.getAttribute('aria-hidden')).toBe('true');
    expect(retry?.disabled).toBe(true);
    expect(retry?.tabIndex).toBe(-1);
  });

  it('keeps cold explicit connection success in the visible connecting state', () => {
    const overlay = new LoadingOverlay();
    overlay.setStartupStatus('error');

    overlay.onReconnected();

    const element = overlayElement();
    expect(element.classList.contains('connecting')).toBe(true);
    expect(element.classList.contains('disconnected')).toBe(false);
    expect(element.hidden).toBe(false);
    expect(element.hasAttribute('inert')).toBe(false);
    expect(element.getAttribute('aria-hidden')).toBe('false');
    expect(element.getAttribute('role')).toBe('group');
    expect(element.getAttribute('aria-live')).toBe('off');
  });

  it('does not cover the last good picture with cold-start presentation', () => {
    const overlay = new LoadingOverlay();
    overlay.onFrame();
    const element = overlayElement();

    overlay.setStartupStatus('error');
    expect(element.classList.contains('visible')).toBe(false);
    expect(element.hidden).toBe(true);
    expect(element.hasAttribute('inert')).toBe(true);
    expect(element.getAttribute('aria-hidden')).toBe('true');

    overlay.setStartupStatus('connecting');
    expect(element.classList.contains('visible')).toBe(false);
    expect(element.hidden).toBe(true);
  });
});
