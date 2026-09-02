// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import { describe, expect, it, vi } from 'vitest';

import { Dvr } from '../editor/dvr';
import { FrameRecorder } from '../editor/recorder';

function space(target: Element, init: KeyboardEventInit = {}): KeyboardEvent {
  const event = new KeyboardEvent('keydown', {
    code: 'Space', bubbles: true, cancelable: true, ...init,
  });
  target.dispatchEvent(event);
  return event;
}

describe('DVR global keyboard ownership', () => {
  it('leaves native controls and reserved chords alone while owning body playback keys', () => {
    document.body.innerHTML = `
      <button id="button"><span id="button-child">Button</span></button>
      <textarea id="textarea"></textarea>
      <div contenteditable="true"><span id="editable-child">Text</span></div>
      <details><summary id="summary"><span id="summary-child">Details</span></summary></details>
      <a id="link" href="#target"><span id="link-child">Link</span></a>
    `;
    const pause = vi.fn();
    const step = vi.fn();
    new Dvr({
      recorder: new FrameRecorder(4),
      onApply: vi.fn(),
      onServerPause: pause,
      onServerStep: step,
      onServerSpeed: vi.fn(),
      onServerReset: vi.fn(),
    });

    for (const id of [
      'button', 'button-child', 'textarea', 'editable-child',
      'summary', 'summary-child', 'link', 'link-child',
    ]) {
      const event = space(document.getElementById(id)!);
      expect(event.defaultPrevented, id).toBe(false);
    }
    for (const modifiers of [{ ctrlKey: true }, { metaKey: true }, { altKey: true }]) {
      const event = space(document.body, modifiers);
      expect(event.defaultPrevented).toBe(false);
    }
    const handled = new KeyboardEvent('keydown', {
      code: 'Space', bubbles: true, cancelable: true,
    });
    handled.preventDefault();
    document.body.dispatchEvent(handled);
    expect(handled.defaultPrevented).toBe(true);
    expect(pause).not.toHaveBeenCalled();

    const transport = space(document.body);
    expect(transport.defaultPrevented).toBe(true);
    expect(pause).toHaveBeenCalledOnce();

    const period = new KeyboardEvent('keydown', {
      code: 'Period', bubbles: true, cancelable: true,
    });
    document.body.dispatchEvent(period);
    expect(period.defaultPrevented).toBe(true);
    expect(step).toHaveBeenCalledOnce();
  });
});
