// ResQ Viz - what a full mixed-domain DVR ring costs to keep
// SPDX-License-Identifier: Apache-2.0
//
// The DVR keeps the last 180 v2 snapshots so an operator can scrub back through
// eighteen seconds of a mixed fleet. That window was chosen against a *wire*
// measurement — the 150-asset reference projection serializes to at most 355,016
// bytes, so 180 of them is ~61 MiB of JSON — and the wire number is not the
// number that matters. What the ring actually holds is the projected
// `SceneSnapshot`: live JavaScript objects, some of which are shared with the
// scene and some of which are not. Sharing makes retention cheaper than the wire
// figure; a snapshot that quietly captured a per-frame typed array, a rendered
// row, or a closure over the previous frame would make it far more expensive,
// and no unit test would notice, because every unit test asserts on values and
// this is a question about references.
//
// So the ring is filled for real, in a real browser, and the retained heap
// either side of filling it is measured through CDP with a forced collection.
//
// Two ceilings come out of that, and they answer different questions. The
// design spec's 128 MiB is the merge gate on the whole window; a per-frame line
// drawn at a snapshot's own serialized size is what notices a snapshot that grew
// while the window stayed the same shape. Both are gates, not tuning knobs: the
// fix for either failing is a compact playback frame, never a larger budget.

import { expect } from '@playwright/test';

import {
  contactRow,
  dvrFrameCount,
  enterReplay,
  goLive,
  type HeapProbe,
  NORMAL_ORIGIN,
  openHeapProbe,
  reportTrack,
  startScenario,
  // `test` comes from the support module, not from `@playwright/test`: it
  // carries the auto fixture that records when the budget started, which is
  // what lets `waitForOperatorConsole` stop — and explain itself — before the
  // budget this spec sets below runs out.
  test,
  waitForAssetRows,
  waitForDomainCounts,
  waitForDvr,
  waitForDvrFrames,
  waitForDvrFramesAtLeast,
  waitForDvrRingBelow,
  waitForOperatorConsole,
} from './support/operatorConsole';

/** The reference mixed fleet: 50 air, 50 ground, 50 surface. */
const SCENARIO = 'mixed-load-150';
const AIR = 50;
const GROUND = 50;
const SURFACE = 50;
const ASSET_COUNT = AIR + GROUND + SURFACE;

/** `DEFAULT_RECORDER_CAPACITIES.v2` — the whole window under measurement. */
const RETAINED_FRAMES = 180;

/**
 * How many frames must be recorded before the scenario start, so that the ring
 * clearing is visible as a drop rather than inferred from a small number.
 */
const FRAMES_BEFORE_START = 10;

/**
 * Merge gate: 128 MiB of retained heap for filling the ring.
 *
 * This is the design spec's budget, and it is a ceiling on the *whole* window
 * rather than on any one part of it. Its arithmetic: the largest 150-asset
 * snapshot serializes to 355,016 bytes, so 180 of them is 63.9 MB — 60.9 MiB —
 * of wire data, and 128 MiB is a shade over twice that, which is the allowance
 * for the same content held as live objects rather than as text.
 *
 * Measured on this build, seven runs at 1440x900: 27.6 to 29.2 MiB, or 21.6% to
 * 22.8% of this gate. (The total moves a little with how full the ring already
 * was at the baseline — 21 to 27 frames across those runs — which is why the
 * per-frame figure below is the steadier statistic.)
 *
 * At four and a half times the measured cost, what this gate catches is a
 * regression of *kind*: a v2 capacity raised past roughly 700 frames, or a
 * snapshot that started holding a rendered row or a per-asset scene object. It
 * would not notice a snapshot that merely doubled, which is why the per-frame
 * gate below exists as well.
 *
 * A failure here is fixed by a compact playback frame, never by a larger number.
 */
const RETAINED_HEAP_LIMIT_BYTES = 128 * 1024 * 1024;

/**
 * Drift gate: 384 KiB of retained heap per recorded snapshot.
 *
 * The claim is one an operator console can be held to for as long as the DVR
 * keeps whole projections: **a recorded frame must not cost more live heap than
 * the bytes it arrived as.** The 150-asset snapshot's serialized maximum is
 * 355,016 bytes; 384 KiB (393,216) is the next binary step above it, so
 * crossing this line means the in-memory copy has grown past its own wire form
 * — which is what happens when a snapshot stops being a structure-shared
 * projection and starts owning per-frame copies.
 *
 * Measured, same seven runs: 185,538 to 192,646 bytes per frame — a 3.8% spread,
 * sitting at 47% to 49% of this ceiling. So ordinary run-to-run variance has an
 * order of magnitude of room below the line, while a snapshot that doubled —
 * recording the raw wire frame *beside* the projection, say, which is exactly
 * the change someone reaches for when replay needs one more field — crosses it.
 * That regression is invisible to the 128 MiB gate above.
 *
 * Both lines were confirmed to fail, not merely to pass: with the ceilings
 * temporarily lowered, this spec reported 191,062 bytes per frame against a
 * 1,024-byte line and 27.6 MiB against a 1 MiB one. A budget nobody has watched
 * fail is a budget nobody knows is connected to anything.
 *
 * Compared against the *maximum* serialized snapshot while the measurement is a
 * mean over the frames filled, which is the conservative direction: a mean
 * cannot exceed a maximum, so the comparison never accuses a frame of being
 * larger than it is.
 */
const RETAINED_BYTES_PER_FRAME_LIMIT = 384 * 1024;

const MIB = 1024 * 1024;

/**
 * Extra budget this spec needs beyond the suite's per-test default.
 *
 * Filling the ring is eighteen wall-clock seconds of 10 Hz broadcasts, and the
 * two forced collections either side of it walk a 150-asset scene — on the
 * GPU-less CI runner a single `page.evaluate` over this page measured 8.2 s.
 * Sixty seconds covers both with room, and is an addition rather than an
 * absolute so it stays correct on either hardware.
 */
const RING_FILL_ALLOWANCE_MS = 60_000;

test.describe('operator console — DVR retained heap', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('stays inside budget with a full 150-asset ring', async ({ page }) => {
    // Filling the ring is 180 frames at 10 Hz — eighteen seconds that no amount
    // of waiting-on-state can compress — on top of booting a 150-asset scene
    // and forcing two full collections over it. So this spec needs *more* than
    // the suite's per-test budget, and the extension is written as one.
    //
    // It used to read `setTimeout(60_000)`, which is smaller than the suite
    // default it was meant to raise. That inversion is what turned a slow CI
    // boot into an unreadable failure: the 45-second console wait plus its
    // diagnostic could not fit inside 60 seconds, so the test timeout fired
    // first and the run reported `page.evaluate: Test timeout of 60000ms
    // exceeded` instead of anything about the console. The waits now size
    // themselves against whatever budget is in force, and the budget is no
    // longer smaller than the work.
    test.setTimeout(test.info().timeout + RING_FILL_ALLOWANCE_MS);

    await page.goto(NORMAL_ORIGIN);
    await waitForOperatorConsole(page);
    await waitForDvr(page);

    // ── The scenario revision empties the ring ─────────────────────────────
    // Recorded frames belong to the run they came from. Frames of the default
    // scenario must not survive into this one: a playhead that could cross the
    // boundary would show a fleet appearing out of nowhere mid-scrub, and — the
    // reason it belongs in *this* spec — a ring that kept them would be holding
    // two scenarios' worth of snapshots against one window's budget.
    await waitForDvrFramesAtLeast(page, FRAMES_BEFORE_START);
    const beforeStart = await dvrFrameCount(page);
    await startScenario(page, SCENARIO);
    const clearedTo = await waitForDvrRingBelow(page, beforeStart);
    expect(
      clearedTo,
      'the new scenario revision must empty the ring, not append to it',
    ).toBeLessThan(beforeStart);

    // ── A full mixed fleet, plus a contact that is in no scenario ──────────
    await waitForAssetRows(page, ASSET_COUNT);
    await waitForDomainCounts(page, { air: AIR, ground: GROUND, surface: SURFACE });
    await reportTrack(page, 'browser-track-1', { x: 220, y: 8, z: -140 });
    await expect(contactRow(page, 'browser-track-1')).toBeVisible();

    // ── Freeze a known baseline ────────────────────────────────────────────
    // Replay stops recording, so the ring stops here and the "before" reading
    // describes a ring of exactly `baselineCount` frames. Reading the count
    // while live would describe a ring that had already moved on, and the growth
    // measured below would be over an unknown starting point.
    await enterReplay(page);
    const baselineCount = await dvrFrameCount(page);
    expect(baselineCount, 'the baseline must be a real, partial ring').toBeGreaterThan(0);
    expect(
      baselineCount,
      'the ring was already full at the baseline, so this measures no growth at all',
    ).toBeLessThan(RETAINED_FRAMES);

    // ── Measure what filling the rest of the ring retains ──────────────────
    // Attached inside the `try` and detached in the `finally`, including when
    // attaching is itself what failed: a CDP session left open outlives the test
    // and holds its target alive, which is a leak in the harness measuring
    // leaks.
    let probe: HeapProbe | undefined;
    try {
      probe = await openHeapProbe(page);
      const before = await probe.sample();

      await goLive(page);
      await waitForDvrFrames(page, RETAINED_FRAMES);
      expect(await dvrFrameCount(page), 'the ring must cap, not keep growing')
        .toBe(RETAINED_FRAMES);

      const after = await probe.sample();
      const delta = after.usedSize - before.usedSize;
      const framesFilled = RETAINED_FRAMES - baselineCount;
      const bytesPerFrame = delta / framesFilled;

      await test.info().attach('dvr-retained-heap.json', {
        contentType: 'application/json',
        body: JSON.stringify(
          {
            assetCount: ASSET_COUNT,
            baselineCount,
            retainedFrames: RETAINED_FRAMES,
            beforeUsedSize: before.usedSize,
            afterUsedSize: after.usedSize,
            delta,
            // Diagnostic only. Array buffers and external strings live outside
            // the JS heap, so a snapshot that moved its cost there would show up
            // here as growth while `delta` stayed flat — worth reading when
            // investigating, never the thing asserted.
            backingStorageSize: after.backingStorageSize,
            limit: RETAINED_HEAP_LIMIT_BYTES,
            bytesPerFrame: Math.round(bytesPerFrame),
            bytesPerFrameLimit: RETAINED_BYTES_PER_FRAME_LIMIT,
          },
          null,
          2,
        ),
      });

      expect(
        delta,
        `Filling the DVR ring from ${baselineCount} to ${RETAINED_FRAMES} frames of `
        + `${ASSET_COUNT} assets retained ${(delta / MIB).toFixed(1)} MiB `
        + `(${before.usedSize} -> ${after.usedSize} bytes used; `
        + `${after.backingStorageSize} bytes backing storage), over the `
        + `${RETAINED_HEAP_LIMIT_BYTES / MIB} MiB budget. `
        + 'The fix is a compact playback frame — stop retaining whole projected '
        + 'snapshots — not a larger budget.',
      ).toBeLessThan(RETAINED_HEAP_LIMIT_BYTES);

      expect(
        bytesPerFrame,
        `Each of the ${framesFilled} snapshots recorded here retained `
        + `${Math.round(bytesPerFrame)} bytes of live heap, past the `
        + `${RETAINED_BYTES_PER_FRAME_LIMIT}-byte line that a snapshot's own `
        + 'serialized maximum (355,016 bytes) sets. A recorded frame now costs '
        + 'more in memory than it did on the wire, which means it is no longer a '
        + 'structure-shared projection — something in the recording path started '
        + 'copying, or holding a reference to, per-frame state. The total is '
        + `still ${(delta / MIB).toFixed(1)} MiB and inside the `
        + `${RETAINED_HEAP_LIMIT_BYTES / MIB} MiB merge gate, which is why this `
        + 'is checked separately.',
      ).toBeLessThan(RETAINED_BYTES_PER_FRAME_LIMIT);
    } finally {
      await probe?.detach();
    }
  });
});
