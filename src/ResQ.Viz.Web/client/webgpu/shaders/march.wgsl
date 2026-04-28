// SPDX-License-Identifier: Apache-2.0
//
// PR #2 — hierarchical brick-map DDA. Top-level grid points at sparse
// 8^3 bricks; rays walk top-level cells, descend into occupied bricks
// for fine-level traversal, ascend on brick exit. Empty top-level cells
// stride 8 voxels at a time, which is the entire reason brick maps win
// for sparse worlds (drone airspace is >99% empty).

struct Camera {
  origin:     vec3<f32>, _p0: f32,
  forward:    vec3<f32>, _p1: f32,
  right:      vec3<f32>, _p2: f32,
  up:         vec3<f32>, _p3: f32,
  resolution: vec2<f32>,
  fov_tan:    f32,
  _p4:        f32,
};
struct Grid {
  size:     vec3<u32>,    // fine grid axis size (e.g. 128)
  top_size: u32,          // top-level axis size (= size / BRICK)
};

@group(0) @binding(0) var<uniform> camera: Camera;
@group(0) @binding(1) var<uniform> grid:   Grid;
@group(0) @binding(2) var<storage, read> top_grid: array<u32>;
@group(0) @binding(3) var output: texture_storage_2d<rgba8unorm, write>;
@group(0) @binding(4) var<storage, read> brick_pool: array<u32>;

const MAX_STEPS: u32 = 1024u;
const BRICK: i32 = 8;

fn in_bounds_top(p: vec3<i32>) -> bool {
  let s = i32(grid.top_size);
  return all(p >= vec3<i32>(0)) && all(p < vec3<i32>(s));
}

// Floor-division by BRICK that handles negative inputs correctly. Used when
// ascending from fine to top-level after a step crosses a brick boundary.
fn floor_div_brick(a: vec3<i32>) -> vec3<i32> {
  return vec3<i32>(floor(vec3<f32>(a) / f32(BRICK)));
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

fn sample_brick(fv: vec3<i32>, cv: vec3<i32>, slot: u32) -> u32 {
  let local = fv - cv * BRICK;
  let li = u32(local.x + local.y * BRICK + local.z * BRICK * BRICK);
  return brick_pool[slot * u32(BRICK * BRICK * BRICK) + li];
}

fn dda(ro: vec3<f32>, rd: vec3<f32>) -> Hit {
  // Replace exact-zero ray-direction components with a tiny epsilon to keep
  // 1/rd finite. (See PR #1 review feedback for the NaN trap on axis-aligned
  // rays starting on integer coordinates.)
  let rd_safe = select(rd, vec3<f32>(1e-30), rd == vec3<f32>(0.0));
  let inv = 1.0 / rd_safe;

  // Clip the ray into the fine-grid AABB before walking, so DDA starts
  // inside even when the camera origin is outside the grid box.
  let box_min = vec3<f32>(0.0);
  let box_max = vec3<f32>(grid.size);
  let aabb = ray_aabb(ro, inv, box_min, box_max);
  let t_enter = max(aabb.x, 0.0);
  let t_exit  = aabb.y;
  if (t_exit < t_enter) {
    return Hit(false, vec3<f32>(0.0), 0u);
  }

  let step    = vec3<i32>(sign(rd_safe));
  let f_delta = abs(inv);
  let c_delta = f_delta * f32(BRICK);
  let ts = i32(grid.top_size);
  // Per-axis fine grid size — supports non-cubic grids for future PRs that
  // stream rectangular terrain regions, not just N×N×N test data.
  let fs = vec3<i32>(grid.size);

  // Top-level (coarse) state — start at the brick containing the entry point.
  let entry = ro + rd_safe * t_enter;
  var cv = clamp(
    vec3<i32>(floor(entry)) / BRICK,
    vec3<i32>(0),
    vec3<i32>(ts - 1),
  );
  var c_tmax = (vec3<f32>(cv + max(step, vec3<i32>(0))) * f32(BRICK) - ro) * inv;

  // Fine-level state — populated lazily on descent.
  var fv: vec3<i32> = vec3<i32>(0);
  var f_tmax: vec3<f32> = vec3<f32>(0.0);
  var slot: u32 = 0u;

  // Track the t at which we crossed into the current cell, so descent can
  // re-derive the fine starting voxel from the actual ray position.
  var t: f32 = t_enter;
  var n = vec3<f32>(0.0);
  var level: u32 = 1u;   // 1 = coarse, 0 = fine

  for (var i = 0u; i < MAX_STEPS; i = i + 1u) {
    if (level == 1u) {
      if (!in_bounds_top(cv)) {
        return Hit(false, vec3<f32>(0.0), 0u);
      }
      let entry_top = top_grid[cv.x + cv.y * ts + cv.z * ts * ts];
      if (entry_top == 0u) {
        // Step the coarse DDA — empty cells stride one whole brick.
        if (c_tmax.x < c_tmax.y && c_tmax.x < c_tmax.z) {
          t = c_tmax.x; c_tmax.x = c_tmax.x + c_delta.x; cv.x = cv.x + step.x;
          n = vec3<f32>(-f32(step.x), 0.0, 0.0);
        } else if (c_tmax.y < c_tmax.z) {
          t = c_tmax.y; c_tmax.y = c_tmax.y + c_delta.y; cv.y = cv.y + step.y;
          n = vec3<f32>(0.0, -f32(step.y), 0.0);
        } else {
          t = c_tmax.z; c_tmax.z = c_tmax.z + c_delta.z; cv.z = cv.z + step.z;
          n = vec3<f32>(0.0, 0.0, -f32(step.z));
        }
        continue;
      }
      // Descend into the occupied brick. Re-derive fv from the current
      // ray position, clamped into the brick's range.
      slot = entry_top - 1u;
      let p = ro + rd_safe * t;
      fv = clamp(
        vec3<i32>(floor(p)),
        cv * BRICK,
        cv * BRICK + vec3<i32>(BRICK - 1),
      );
      f_tmax = (vec3<f32>(fv + max(step, vec3<i32>(0))) - ro) * inv;
      level = 0u;
      continue;
    }

    // Fine level — sample the current voxel through the brick pool.
    let m = sample_brick(fv, cv, slot);
    if (m != 0u) {
      return Hit(true, n, m);
    }

    // Step the fine DDA.
    if (f_tmax.x < f_tmax.y && f_tmax.x < f_tmax.z) {
      t = f_tmax.x; f_tmax.x = f_tmax.x + f_delta.x; fv.x = fv.x + step.x;
      n = vec3<f32>(-f32(step.x), 0.0, 0.0);
    } else if (f_tmax.y < f_tmax.z) {
      t = f_tmax.y; f_tmax.y = f_tmax.y + f_delta.y; fv.y = fv.y + step.y;
      n = vec3<f32>(0.0, -f32(step.y), 0.0);
    } else {
      t = f_tmax.z; f_tmax.z = f_tmax.z + f_delta.z; fv.z = fv.z + step.z;
      n = vec3<f32>(0.0, 0.0, -f32(step.z));
    }

    // If the step left this brick, ascend to the top level. The new top
    // cell's emptiness gets re-checked in the next iteration; if it's also
    // occupied, we'll descend again immediately.
    let new_cv = floor_div_brick(fv);
    if (any(new_cv != cv)) {
      cv = new_cv;
      c_tmax = (vec3<f32>(cv + max(step, vec3<i32>(0))) * f32(BRICK) - ro) * inv;
      level = 1u;
    }

    // Out-of-grid termination — only after a step. The initial fv is always
    // inside (we descended from a valid cv). Per-axis check supports
    // non-cubic grids.
    if (any(fv < vec3<i32>(0)) || any(fv >= fs)) {
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
