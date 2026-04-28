// SPDX-License-Identifier: Apache-2.0
//
// Step 0 — dense-grid DDA voxel marcher. One ray per pixel, hard hit on
// non-zero voxel. Replaced by hierarchical brick-map marcher in PR #2.

struct Camera {
  origin:     vec3<f32>, _p0: f32,
  forward:    vec3<f32>, _p1: f32,
  right:      vec3<f32>, _p2: f32,
  up:         vec3<f32>, _p3: f32,
  resolution: vec2<f32>,
  fov_tan:    f32,
  _p4:        f32,
};
struct Grid { size: vec3<u32>, _p: u32 };

@group(0) @binding(0) var<uniform> camera: Camera;
@group(0) @binding(1) var<uniform> grid:   Grid;
@group(0) @binding(2) var<storage, read> voxels: array<u32>;
@group(0) @binding(3) var output: texture_storage_2d<rgba8unorm, write>;

const MAX_STEPS: u32 = 512u;

fn idx(p: vec3<i32>) -> u32 {
  let s = vec3<i32>(grid.size);
  return u32(p.x + p.y * s.x + p.z * s.x * s.y);
}

fn in_bounds(p: vec3<i32>) -> bool {
  let s = vec3<i32>(grid.size);
  return all(p >= vec3<i32>(0)) && all(p < s);
}

struct Hit {
  hit:    bool,
  normal: vec3<f32>,
  mat:    u32,
};

// Slab AABB intersection. Returns vec2(t_enter, t_exit). The ray misses the
// box iff t_exit < max(t_enter, 0).
fn ray_aabb(ro: vec3<f32>, inv: vec3<f32>, box_min: vec3<f32>, box_max: vec3<f32>) -> vec2<f32> {
  let t1 = (box_min - ro) * inv;
  let t2 = (box_max - ro) * inv;
  let t_lo = min(t1, t2);
  let t_hi = max(t1, t2);
  let t_enter = max(max(t_lo.x, t_lo.y), t_lo.z);
  let t_exit  = min(min(t_hi.x, t_hi.y), t_hi.z);
  return vec2<f32>(t_enter, t_exit);
}

fn dda(ro: vec3<f32>, rd: vec3<f32>) -> Hit {
  // Replace exact-zero ray-direction components with a tiny epsilon to keep
  // 1/rd finite. Otherwise 1/0 = ∞ propagates into t_max as 0*∞ = NaN, and
  // the min-axis comparisons below behave undefined-ly for axis-aligned rays
  // starting on integer coordinates.
  let rd_safe = select(rd, vec3<f32>(1e-30), rd == vec3<f32>(0.0));
  let inv = 1.0 / rd_safe;

  // Most camera rays start outside the grid AABB. Clip the ray into the
  // grid first, then run DDA from the entry point — the previous version
  // bailed out on the immediate out-of-bounds check before walking in.
  let box_min = vec3<f32>(0.0);
  let box_max = vec3<f32>(grid.size);
  let aabb = ray_aabb(ro, inv, box_min, box_max);
  let t_enter = max(aabb.x, 0.0);
  let t_exit  = aabb.y;
  if (t_exit < t_enter) {
    return Hit(false, vec3<f32>(0.0), 0u);
  }

  let entry = ro + rd_safe * t_enter;
  let s = vec3<i32>(grid.size);
  // Clamp to guard against floating-point drift past the boundary.
  var v = clamp(vec3<i32>(floor(entry)), vec3<i32>(0), s - vec3<i32>(1));

  let step = vec3<i32>(sign(rd_safe));
  let t_delta = abs(inv);
  // t_max measured from the original ray origin (so step accumulation stays
  // consistent with t_delta). The starting cell `v` is already inside.
  var t_max = (vec3<f32>(v + max(step, vec3<i32>(0))) - ro) * inv;

  var n = vec3<f32>(0.0);
  for (var i = 0u; i < MAX_STEPS; i = i + 1u) {
    let m = voxels[idx(v)];
    if (m != 0u) {
      return Hit(true, n, m);
    }
    if (t_max.x < t_max.y && t_max.x < t_max.z) {
      t_max.x = t_max.x + t_delta.x;
      v.x = v.x + step.x;
      n = vec3<f32>(-f32(step.x), 0.0, 0.0);
    } else if (t_max.y < t_max.z) {
      t_max.y = t_max.y + t_delta.y;
      v.y = v.y + step.y;
      n = vec3<f32>(0.0, -f32(step.y), 0.0);
    } else {
      t_max.z = t_max.z + t_delta.z;
      v.z = v.z + step.z;
      n = vec3<f32>(0.0, 0.0, -f32(step.z));
    }
    // Bounds check AFTER the step: we exit only when we step OUT of the
    // grid, never on the initial cell.
    if (!in_bounds(v)) {
      return Hit(false, vec3<f32>(0.0), 0u);
    }
  }
  return Hit(false, vec3<f32>(0.0), 0u);
}

@compute @workgroup_size(8, 8, 1)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
  let dim = vec2<u32>(camera.resolution);
  if (gid.x >= dim.x || gid.y >= dim.y) {
    return;
  }
  let uv = (vec2<f32>(gid.xy) + 0.5) / camera.resolution;
  let ndc = uv * 2.0 - 1.0;
  let aspect = camera.resolution.x / camera.resolution.y;
  let dir = normalize(
    camera.forward
    + camera.right * ndc.x * aspect * camera.fov_tan
    - camera.up    * ndc.y * camera.fov_tan
  );
  let h = dda(camera.origin, dir);
  var col = vec3<f32>(0.05, 0.07, 0.10);
  if (h.hit) {
    let l = max(dot(h.normal, normalize(vec3<f32>(0.4, 0.8, 0.3))), 0.0);
    col = vec3<f32>(0.8, 0.7, 0.5) * (0.2 + 0.8 * l);
  }
  textureStore(output, vec2<i32>(gid.xy), vec4<f32>(col, 1.0));
}
