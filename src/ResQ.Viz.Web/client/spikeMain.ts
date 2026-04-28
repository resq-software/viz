// ResQ Viz — WebGPU Step 0 spike (dev-only, hidden route at /spike.html)
// SPDX-License-Identifier: Apache-2.0
//
// Validates the WebGPU stack before brick-map work in PR #2+. Renders a
// synthetic heightmap via dense-grid DDA. No SignalR, no Three.js, no
// production-viz coupling.

import marchSrc from './webgpu/shaders/march.wgsl?raw';
import blitSrc from './webgpu/shaders/blit.wgsl?raw';
import { initDevice } from './webgpu/device';

const N = 128;
const TILE = 8;

function $<T extends Element>(sel: string): T {
  const el = document.querySelector(sel);
  if (!el) throw new Error(`${sel} not found`);
  return el as T;
}

const statusEl = $<HTMLElement>('#status');
const canvas = $<HTMLCanvasElement>('#gpu');

function setStatus(msg: string): void {
  statusEl.textContent = msg;
}

function buildHeightmap(n: number): Uint32Array {
  const v = new Uint32Array(n * n * n);
  for (let z = 0; z < n; z++) {
    for (let x = 0; x < n; x++) {
      const h = Math.floor(n * 0.4 + 6 * Math.sin(x * 0.12) * Math.cos(z * 0.12));
      for (let y = 0; y < h; y++) v[x + y * n + z * n * n] = 1;
    }
  }
  return v;
}

type Vec3 = [number, number, number];

function norm(v: Vec3): Vec3 {
  const l = Math.hypot(v[0], v[1], v[2]) || 1;
  return [v[0] / l, v[1] / l, v[2] / l];
}

function cross(a: Vec3, b: Vec3): Vec3 {
  return [
    a[1] * b[2] - a[2] * b[1],
    a[2] * b[0] - a[0] * b[2],
    a[0] * b[1] - a[1] * b[0],
  ];
}

async function main(): Promise<void> {
  setStatus('checking WebGPU…');
  const init = await initDevice();
  if (!init.ok) {
    setStatus(`WebGPU unavailable: ${init.reason}`);
    return;
  }
  const { device, hasTimestamp } = init;

  const ctx = canvas.getContext('webgpu');
  if (!ctx) {
    setStatus('canvas.getContext("webgpu") returned null');
    return;
  }
  const format = navigator.gpu.getPreferredCanvasFormat();
  ctx.configure({ device, format, alphaMode: 'opaque' });

  const voxels = buildHeightmap(N);
  const voxelBuf = device.createBuffer({
    size: voxels.byteLength,
    usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST,
  });
  device.queue.writeBuffer(voxelBuf, 0, voxels);

  const camBuf = device.createBuffer({
    size: 80,
    usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
  });
  const gridBuf = device.createBuffer({
    size: 16,
    usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST,
  });
  device.queue.writeBuffer(gridBuf, 0, new Uint32Array([N, N, N, 0]));

  const computePipe = device.createComputePipeline({
    layout: 'auto',
    compute: {
      module: device.createShaderModule({ code: marchSrc }),
      entryPoint: 'main',
    },
  });
  const blitMod = device.createShaderModule({ code: blitSrc });
  const blitPipe = device.createRenderPipeline({
    layout: 'auto',
    vertex: { module: blitMod, entryPoint: 'vs' },
    fragment: { module: blitMod, entryPoint: 'fs', targets: [{ format }] },
    primitive: { topology: 'triangle-list' },
  });
  const sampler = device.createSampler({ magFilter: 'nearest', minFilter: 'nearest' });

  let outTex: GPUTexture | null = null;
  let outView: GPUTextureView | null = null;
  let computeBG: GPUBindGroup | null = null;
  let blitBG: GPUBindGroup | null = null;
  function resize(): void {
    const dpr = Math.min(window.devicePixelRatio, 2);
    const w = Math.max(1, Math.floor(canvas.clientWidth * dpr));
    const h = Math.max(1, Math.floor(canvas.clientHeight * dpr));
    if (canvas.width === w && canvas.height === h && outTex) return;
    canvas.width = w;
    canvas.height = h;
    if (outTex) outTex.destroy();
    outTex = device.createTexture({
      size: [w, h],
      format: 'rgba8unorm',
      usage: GPUTextureUsage.STORAGE_BINDING | GPUTextureUsage.TEXTURE_BINDING,
    });
    outView = outTex.createView();
    // Bind groups reference outView, so they must be rebuilt whenever the
    // storage texture is recreated. Buffers and the sampler are stable.
    computeBG = device.createBindGroup({
      layout: computePipe.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: { buffer: camBuf } },
        { binding: 1, resource: { buffer: gridBuf } },
        { binding: 2, resource: { buffer: voxelBuf } },
        { binding: 3, resource: outView },
      ],
    });
    blitBG = device.createBindGroup({
      layout: blitPipe.getBindGroupLayout(0),
      entries: [
        { binding: 0, resource: outView },
        { binding: 1, resource: sampler },
      ],
    });
  }
  resize();
  window.addEventListener('resize', resize);

  // Reusable scratch for the camera uniform — avoids per-frame allocation.
  const camData = new Float32Array(20);
  function writeCamera(t: number): void {
    const r = N * 1.6;
    const ox = N / 2 + r * Math.cos(t * 0.25);
    const oz = N / 2 + r * Math.sin(t * 0.25);
    const oy = N * 0.7;
    const fwd = norm([N / 2 - ox, N * 0.4 - oy, N / 2 - oz]);
    const right = norm(cross(fwd, [0, 1, 0]));
    const up = cross(right, fwd);
    camData[0]  = ox;       camData[1]  = oy;       camData[2]  = oz;
    camData[4]  = fwd[0];   camData[5]  = fwd[1];   camData[6]  = fwd[2];
    camData[8]  = right[0]; camData[9]  = right[1]; camData[10] = right[2];
    camData[12] = up[0];    camData[13] = up[1];    camData[14] = up[2];
    camData[16] = canvas.width;
    camData[17] = canvas.height;
    camData[18] = Math.tan(Math.PI / 6);
    device.queue.writeBuffer(camBuf, 0, camData);
  }

  setStatus(hasTimestamp ? 'running (timestamp on)' : 'running');

  function frame(t: number): void {
    resize();
    if (!outView || !ctx || !computeBG || !blitBG) return;
    writeCamera(t / 1000);

    const enc = device.createCommandEncoder();
    const cp = enc.beginComputePass();
    cp.setPipeline(computePipe);
    cp.setBindGroup(0, computeBG);
    cp.dispatchWorkgroups(
      Math.ceil(canvas.width / TILE),
      Math.ceil(canvas.height / TILE),
      1,
    );
    cp.end();

    const rp = enc.beginRenderPass({
      colorAttachments: [{
        view: ctx.getCurrentTexture().createView(),
        loadOp: 'clear',
        storeOp: 'store',
        clearValue: { r: 0, g: 0, b: 0, a: 1 },
      }],
    });
    rp.setPipeline(blitPipe);
    rp.setBindGroup(0, blitBG);
    rp.draw(3);
    rp.end();

    device.queue.submit([enc.finish()]);
    requestAnimationFrame(frame);
  }
  requestAnimationFrame(frame);
}

main().catch((err: unknown) => {
  console.error(err);
  const msg = err instanceof Error ? err.message : String(err);
  setStatus(`error: ${msg}`);
});
