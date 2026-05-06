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

import {
    initAnalytics,
    RESQ_SUBDOMAIN_ALLOWLIST,
    resolveResqCookieDomain,
    sanitizeGa4Id,
} from "@resq-sw/analytics";

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

    const cookieDomain =
        typeof window === "undefined"
            ? undefined
            : resolveResqCookieDomain(window.location.hostname);

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
                      domains: [...RESQ_SUBDOMAIN_ALLOWLIST],
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
