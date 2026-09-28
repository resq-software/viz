<!--
  Copyright 2026 ResQ Systems, Inc.
  SPDX-License-Identifier: Apache-2.0
-->

# Accessibility

This statement covers the ResQ Viz console at <https://viz.resq.software>.

## Target

[WCAG 2.2](https://www.w3.org/TR/WCAG22/) Level AA.

## Status

Not yet formally audited. No manual or third-party accessibility audit has been done.

What is checked automatically:

- **axe-core in a real browser.** `src/ResQ.Viz.Web/e2e/accessibility.spec.ts` loads the connected console and the privacy dialog in Chromium. CI fails on any violation of the WCAG 2.0, 2.1 or 2.2 A/AA rules that axe-core implements. It reports none today.
- **Focus, reachability and hit targets.** The Playwright suite checks that the console's controls can be reached and clicked, and that Tab cannot move into a closed panel.

## Known issues

- **The 3D scene is a canvas.** Screen readers get a live telemetry summary and the fleet roster, not the scene itself.
- **Choosing a destination on the map needs a pointer.** Commands that take a destination, such as sending an asset somewhere, use a map picker you click.
- **Contrast over the scene is not checked.** The CI browser run does not draw the 3D scene, so axe-core cannot measure panels and labels against the terrain behind them.
- **Automated checks find only part of what WCAG covers.** Screen-reader flows have not been tested by hand.

## Report a barrier

[Open an issue](https://github.com/resq-software/viz/issues/new) describing what you tried to do, where in the console, and the browser and assistive technology you used. If you would rather not post publicly, email **contact@resq.software**.
