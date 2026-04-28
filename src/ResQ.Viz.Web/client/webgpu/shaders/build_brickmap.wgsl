// SPDX-License-Identifier: Apache-2.0
//
// Build pass for the brick-map storage. One thread per top-level cell:
// scan the BRICK^3 children, mark the cell empty if all voxels are zero,
// otherwise atomically allocate a slot in the brick pool and copy the
// voxels in. Run once at startup (or whenever the dense voxel buffer
// changes) — see brickmap.ts:buildBrickMap.

struct Sizes {
  fine:  u32,   // dense grid axis size (e.g. 128)
  brick: u32,   // brick axis size (e.g. 8)
  top:   u32,   // top-level axis size (= fine / brick)
  _pad:  u32,
};

@group(0) @binding(0) var<storage, read>            voxels:     array<u32>;
@group(0) @binding(1) var<storage, read_write>      top_grid:   array<u32>;
@group(0) @binding(2) var<storage, read_write>      brick_pool: array<u32>;
@group(0) @binding(3) var<storage, read_write>      counter:    atomic<u32>;
@group(0) @binding(4) var<uniform>                  s:          Sizes;

@compute @workgroup_size(4, 4, 4)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
  if (any(gid >= vec3<u32>(s.top))) {
    return;
  }
  let f = s.fine;
  let b = s.brick;
  let base = gid * b;

  // Pass 1 — does this brick contain any solid voxel?
  var any_solid: bool = false;
  for (var dz = 0u; dz < b; dz = dz + 1u) {
    for (var dy = 0u; dy < b; dy = dy + 1u) {
      for (var dx = 0u; dx < b; dx = dx + 1u) {
        let p = base + vec3<u32>(dx, dy, dz);
        if (voxels[p.x + p.y * f + p.z * f * f] != 0u) {
          any_solid = true;
        }
      }
    }
  }

  let top_idx = gid.x + gid.y * s.top + gid.z * s.top * s.top;
  if (!any_solid) {
    top_grid[top_idx] = 0u;
    return;
  }

  // Allocate a slot and record the +1-shifted index so 0 stays the
  // sentinel for "empty cell" in the top grid.
  let slot = atomicAdd(&counter, 1u);
  top_grid[top_idx] = slot + 1u;

  // Pass 2 — copy this brick's voxels into the pool at slot * brick^3.
  let pool_base = slot * (b * b * b);
  for (var dz = 0u; dz < b; dz = dz + 1u) {
    for (var dy = 0u; dy < b; dy = dy + 1u) {
      for (var dx = 0u; dx < b; dx = dx + 1u) {
        let p = base + vec3<u32>(dx, dy, dz);
        let src = voxels[p.x + p.y * f + p.z * f * f];
        let local = dx + dy * b + dz * b * b;
        brick_pool[pool_base + local] = src;
      }
    }
  }
}
