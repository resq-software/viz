/**
 * Copyright 2026 ResQ Systems, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

/**
 * Analytics bootstrap for the viz SPA.
 *
 * Vanilla TS (not React) so we use `@resq-sw/analytics`'s framework-
 * agnostic `initAnalytics()` directly instead of the `<AnalyticsProvider>`
 * we wired into landing + research. The package's lazy `posthog-js`
 * import + GA4 script injection still happen exactly the same way.
 *
 * Cross-subdomain identity:
 *   - PostHog: same project as resq.software / research. Cookie domain is
 *     pinned to `.resq.software` only when the current host actually
 *     belongs to that registrable root, so a single `distinct_id` follows
 *     users across the three subdomains.
 *   - GA4: separate property per subdomain (operator decision). Linker
 *     domains are still listed for forward-compat — gtag treats them as a
 *     no-op when the visited subdomain reports to a different property.
 *
 * Env vars (build-time, set in the deployment pipeline):
 *   - VITE_POSTHOG_KEY   PostHog project API key (`phc_...`).
 *   - VITE_POSTHOG_HOST  Same-origin proxy path. Optional. Defaults to
 *                        direct ingestion since viz has no Next-style
 *                        rewrite layer; set to a CF Workers proxy path
 *                        if/when one is provisioned.
 *   - VITE_GA4_ID        GA4 Measurement ID (`G-XXXXXXX`). Optional.
 *
 * If neither key is set, the singleton boots in `disabled` mode so local
 * dev doesn't dirty production.
 */

import { initAnalytics } from "@resq-sw/analytics";

/**
 * Cross-subdomain allow-list for the GA4 linker. Currently duplicated
 * across `resq-software/landing`, `resq-software/research`, and this repo
 * — the shared list of TS-surface subdomains lives in three places by
 * accident of how the rollout shipped. Follow-up: hoist into
 * `@resq-sw/analytics` as a `RESQ_SUBDOMAIN_ALLOWLIST` const export so
 * adding a fourth subdomain is a single-package version bump instead of
 * three coordinated edits. Tracked in operator notes.
 */
const SUBDOMAIN_ALLOWLIST = [
    "resq.software",
    "research.resq.software",
    "viz.resq.software",
];

/**
 * GA4 Measurement IDs are `G-` followed by 6–32 alphanumerics. Anything
 * else returns null and the loader is skipped. Mirror of the resq.software
 * sanitiser that closed CodeQL alert #34 — even though the env value is
 * build-time controlled, the regex makes the taint flow into the inline
 * script provably safe.
 */
const GA4_ID_PATTERN = /^G-[A-Z0-9]{6,32}$/;
function sanitizeGa4Id(id: string | undefined): string | null {
    if (!id) return null;
    return GA4_ID_PATTERN.test(id) ? id : null;
}

/**
 * Resolve the production cookie domain only when the current host
 * actually lives under `resq.software`. Cloudflare preview / `localhost`
 * get `undefined` so the browser doesn't reject the cookie with a domain
 * mismatch.
 */
function resolveCookieDomain(): string | undefined {
    if (typeof window === "undefined") return undefined;
    const host = window.location.hostname;
    if (host === "resq.software" || host.endsWith(".resq.software")) {
        return ".resq.software";
    }
    return undefined;
}

/**
 * Boot the analytics singleton once on app start. Safe to call before the
 * rest of the app initialises — `posthog-js` is dynamically imported
 * inside the package so this never blocks the main bundle.
 *
 * Call exactly once from `client/app.ts`'s entry path.
 */
export function bootstrapAnalytics(): void {
    const posthogKey = import.meta.env.VITE_POSTHOG_KEY as string | undefined;
    const ga4IdRaw = import.meta.env.VITE_GA4_ID as string | undefined;
    const ga4Id = sanitizeGa4Id(ga4IdRaw);
    const posthogHost = (import.meta.env.VITE_POSTHOG_HOST as string | undefined) ?? undefined;

    if (!posthogKey && !ga4Id) {
        // No keys configured — early-return so local dev / preview deploys
        // don't dirty production analytics. The singleton's `track` /
        // `identify` exports stay safe no-ops even without init.
        return;
    }

    const cookieDomain = resolveCookieDomain();

    initAnalytics({
        ...(cookieDomain ? { cookieDomain } : {}),
        ...(posthogKey
            ? {
                  posthog: {
                      key: posthogKey,
                      ...(posthogHost ? { host: posthogHost } : {}),
                      uiHost: "https://us.posthog.com",
                  },
              }
            : {}),
        ...(ga4Id
            ? {
                  ga4: {
                      measurementId: ga4Id,
                      domains: SUBDOMAIN_ALLOWLIST,
                  },
              }
            : {}),
    }).catch((err) => {
        // `initAnalytics` returns a Promise (it dynamically imports
        // `posthog-js`). The package fails soft internally — the
        // singleton's track/identify exports stay safe no-ops on init
        // failure — but surfacing the error here makes prod debugging
        // tractable when, say, a CSP rule blocks the dynamic import.
        // eslint-disable-next-line no-console -- intentional surface for
        // ops visibility; gated by the early-return above so it only
        // fires when at least one analytics key was actually configured.
        console.warn("[analytics] initAnalytics failed:", err);
    });
}
