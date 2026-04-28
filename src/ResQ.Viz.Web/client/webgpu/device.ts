// SPDX-License-Identifier: Apache-2.0
//
// WebGPU adapter/device init with capability detection. Returns a
// discriminated union so callers can degrade gracefully when WebGPU is
// unavailable.

export type InitOk = {
  ok: true;
  adapter: GPUAdapter;
  device: GPUDevice;
  hasTimestamp: boolean;
};

export type InitErr = {
  ok: false;
  reason: string;
};

export type InitResult = InitOk | InitErr;

export async function initDevice(): Promise<InitResult> {
  if (!('gpu' in navigator)) {
    return {
      ok: false,
      reason: 'navigator.gpu missing — browser does not support WebGPU',
    };
  }

  let adapter: GPUAdapter | null = null;
  try {
    adapter = await navigator.gpu.requestAdapter();
  } catch (e) {
    return { ok: false, reason: `requestAdapter failed: ${describe(e)}` };
  }
  if (!adapter) {
    return { ok: false, reason: 'no GPU adapter available' };
  }

  const hasTimestamp = adapter.features.has('timestamp-query');
  const requiredFeatures: GPUFeatureName[] = hasTimestamp
    ? ['timestamp-query']
    : [];

  let device: GPUDevice;
  try {
    device = await adapter.requestDevice({ requiredFeatures });
  } catch (e) {
    return { ok: false, reason: `requestDevice failed: ${describe(e)}` };
  }

  void device.lost.then(info => {
    console.warn('[webgpu] device lost:', info.reason, info.message);
  });

  return { ok: true, adapter, device, hasTimestamp };
}

function describe(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
