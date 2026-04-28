// SPDX-License-Identifier: Apache-2.0
//
// Fullscreen blit: samples the marcher's storage texture into the swapchain
// via a single fullscreen triangle.

@group(0) @binding(0) var src: texture_2d<f32>;
@group(0) @binding(1) var smp: sampler;

struct VsOut {
  @builtin(position) pos: vec4<f32>,
  @location(0)       uv:  vec2<f32>,
};

@vertex
fn vs(@builtin(vertex_index) i: u32) -> VsOut {
  let x = f32((i << 1u) & 2u);
  let y = f32(i & 2u);
  var o: VsOut;
  o.pos = vec4<f32>(x * 2.0 - 1.0, 1.0 - y * 2.0, 0.0, 1.0);
  o.uv  = vec2<f32>(x, y);
  return o;
}

@fragment
fn fs(in: VsOut) -> @location(0) vec4<f32> {
  return textureSample(src, smp, in.uv);
}
