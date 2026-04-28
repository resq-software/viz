// SPDX-License-Identifier: Apache-2.0
//
// Build pass for the brick-map storage. One thread per top-level cell:
// scan the BRICK^3 children, mark the cell empty if all voxels are zero,
// otherwise atomically allocate a slot in the brick pool and copy the
// voxels in. Run once at startup (or whenever the dense voxel buffer
// changes) — see brickmap.ts:buildBrickMap.

struct Sizes {
  fine:       u32,   // dense grid axis size (e.g. 128)
  brick:      u32,   // brick axis size (e.g. 8)
  top:        u32,   // top-level axis size (= fine / brick)
  max_bricks: u32,   // pool capacity in bricks; bounds-checked on atomicAdd
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
  let top_idx = gid.x + gid.y * s.top + gid.z * s.top * s.top;

  // Pass 1 — linearized scan with early exit. The previous nested loop
  // ran the full BRICK^3 = 512 reads even after finding a solid voxel.
  var any_solid: bool = false;
  let total = b * b * b;
  for (var i = 0u; i < total; i = i + 1u) {
    let dx = i % b;
    let dy = (i / b) % b;
    let dz = i / (b * b);
    let p = base + vec3<u32>(dx, dy, dz);
    if (voxels[p.x + p.y * f + p.z * f * f] != 0u) {
      any_solid = true;
      break;
    }
  }

  if (!any_solid) {
    top_grid[top_idx] = 0u;
    return;
  }

  // Atomically allocate a pool slot. If we exceed pool capacity, mark the
  // cell empty rather than corrupting brick_pool with an OOB write. The
  // counter increment "leaks" in that case — callers can detect pool
  // overflow by reading back `counter` and comparing to `max_bricks`.
  let slot = atomicAdd(&counter, 1u);
  if (slot >= s.max_bricks) {
    top_grid[top_idx] = 0u;
    return;
  }

  // Record the +1-shifted index so 0 stays the sentinel for "empty cell".
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
