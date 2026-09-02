// @vitest-environment happy-dom
// SPDX-License-Identifier: Apache-2.0

import * as THREE from 'three';
import { describe, expect, it, vi } from 'vitest';

import { UnityCamera } from '../cameraControl';
import { Dvr } from '../editor/dvr';
import { FrameRecorder } from '../editor/recorder';

describe('camera and transport keyboard ownership', () => {
  it('reserves Space for transport while preserving movement and Shift speed', () => {
    const camera = new THREE.PerspectiveCamera();
    camera.position.set(0, 100, 100);
    const canvas = document.createElement('canvas');
    document.body.appendChild(canvas);
    const controller = new UnityCamera(camera, canvas);
    const pause = vi.fn();
    new Dvr({
      recorder: new FrameRecorder(4),
      onApply: vi.fn(),
      onServerPause: pause,
      onServerStep: vi.fn(),
      onServerSpeed: vi.fn(),
      onServerReset: vi.fn(),
    });
    canvas.dispatchEvent(new MouseEvent('mousedown', { button: 2, bubbles: true }));
    const initialY = camera.position.y;

    const space = new KeyboardEvent('keydown', {
      code: 'Space', bubbles: true, cancelable: true,
    });
    document.body.dispatchEvent(space);
    controller.update(1);

    expect(space.defaultPrevented).toBe(true);
    expect(pause).toHaveBeenCalledOnce();
    expect(camera.position.y).toBeCloseTo(initialY, 6);

    document.body.dispatchEvent(new KeyboardEvent('keyup', { code: 'Space', bubbles: true }));
    document.body.dispatchEvent(new KeyboardEvent('keydown', { code: 'ShiftLeft', bubbles: true }));
    document.body.dispatchEvent(new KeyboardEvent('keydown', { code: 'KeyQ', bubbles: true }));
    controller.update(1);

    expect(camera.position.y - initialY).toBeGreaterThanOrEqual(79);
  });
});
