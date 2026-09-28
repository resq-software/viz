<!--
  Copyright 2026 ResQ Systems, Inc.
  SPDX-License-Identifier: Apache-2.0
-->

# Privacy

This notice covers the ResQ Viz console at <https://viz.resq.software>. Visitors see the same text in the console under **Settings → Privacy settings** (`src/ResQ.Viz.Web/client/index.html`, `#privacy-dialog`). Keep the two in step.

## Analytics is opt-in

ResQ Viz loads no analytics until you accept. If you decline, or do not choose, no analytics script is loaded and nothing is sent to an analytics provider.

## If you accept

ResQ Systems, Inc. uses two services:

- **Google Analytics 4** (Google) receives the pages you view and, if Google's enhanced measurement is switched on, interactions such as scrolls and outbound clicks. It also receives browser, device and screen details, the referring page, and an approximate location derived from your IP address. Google Analytics 4 does not log or store IP addresses. Identifiers are kept in `_ga` cookies.
- **PostHog** (PostHog, Inc., US region) receives the pages you view, and clicks and other interactions PostHog records automatically, but not what you type. It also receives browser, device and screen details, the referring page, and an approximate location derived from your IP address. A random identifier is kept in local storage and in a cookie shared across resq.software sites. Optional PostHog features, such as session recording, run only if they are switched on for the project.

## Purpose and legal basis

To understand how the console is used and improve it. The legal basis is your consent.

## Retention and location

Data is kept as configured with each provider. Google and PostHog may process it in the United States.

## Changing your mind

Choose **Decline** in the banner, or later under **Settings → Privacy settings**. The console then stops PostHog capture, turns Google Analytics off for the page and deletes the `_ga` cookies. Clearing this site's data in your browser also removes your choice, and the console asks again.

Your choice is stored in your browser's local storage under `resq-analytics-consent`.

## Storage the console needs

A `viz_session` cookie ties your browser to its simulation room, and local storage keeps your console settings and your analytics choice. They are not used for analytics.

## Contact

Email **contact@resq.software** or [open an issue](https://github.com/resq-software/viz/issues).
