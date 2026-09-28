/**
 * Copyright 2026 ResQ Systems, Inc.
 * SPDX-License-Identifier: Apache-2.0
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
 * Analytics bootstrap for the viz SPA — opt-in only.
 *
 * Nothing analytics-related loads before the visitor accepts: no gtag.js
 * script, no `posthog-js` import, no request. On a first visit (no stored
 * decision) the consent banner asks; "Decline" keeps everything unloaded and
 * "Accept" calls `initAnalytics()`. A stored acceptance boots straight away on
 * later visits, and Settings → Privacy settings changes the choice at any time.
 *
 * Vanilla TS (not React), so it drives `@resq-systems/analytics`'s
 * framework-agnostic `initAnalytics()` directly. The package lazily imports
 * `posthog-js` and injects the GA4 script only when that call runs.
 *
 * Cross-subdomain identity (after consent):
 *   - PostHog: the cookie domain is pinned to `.resq.software` only when the
 *     current host belongs to that registrable root, so one `distinct_id`
 *     follows a visitor across the org's subdomains.
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
 * If neither key is set there is nothing to consent to: no banner is shown and
 * nothing loads, so local dev and preview deploys stay clean.
 */

import {
    analytics,
    type AnalyticsConfig,
    initAnalytics,
    RESQ_SUBDOMAIN_ALLOWLIST,
    resolveResqCookieDomain,
    sanitizeGa4Id,
} from "@resq-systems/analytics";
import { getLogger } from "./log";
import { type ConsentDecision, readConsent, writeConsent } from "./privacy/consent";

const log = getLogger("analytics");

/** Build-time analytics settings, injectable for tests. */
export interface AnalyticsEnv {
    readonly VITE_POSTHOG_KEY?: string;
    readonly VITE_POSTHOG_HOST?: string;
    readonly VITE_GA4_ID?: string;
}

/**
 * The provider config this build would use, or `null` when no provider key is
 * configured.
 */
export function resolveAnalyticsConfig(
    env: AnalyticsEnv = import.meta.env as AnalyticsEnv,
): AnalyticsConfig | null {
    const posthogKey = env.VITE_POSTHOG_KEY || undefined;
    const ga4Id = sanitizeGa4Id(env.VITE_GA4_ID);
    const posthogHost = env.VITE_POSTHOG_HOST || undefined;
    if (!posthogKey && !ga4Id) return null;

    const cookieDomain =
        typeof window === "undefined"
            ? undefined
            : resolveResqCookieDomain(window.location.hostname);

    return {
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
    };
}

let started = false;

/** Load the providers. Only ever called after the visitor accepted. */
function start(config: AnalyticsConfig): void {
    if (started) return;
    started = true;
    initAnalytics(config).catch((err: unknown) => {
        // `initAnalytics` dynamically imports `posthog-js`. The package fails
        // soft (track/identify stay no-ops), but surfacing the error makes a
        // CSP rule blocking the import debuggable in production.
        log.warn("[analytics] initAnalytics failed", { error: err });
    });
}

/** GA4's documented per-stream opt-out flag. */
function setGa4Disabled(config: AnalyticsConfig, disabled: boolean): void {
    if (!config.ga4) return;
    (window as unknown as Record<string, unknown>)[`ga-disable-${config.ga4.measurementId}`] = disabled;
}

/** Delete GA4's `_ga` / `_ga_<stream>` cookies on this host and the shared domain. */
function clearGa4Cookies(config: AnalyticsConfig): void {
    const names = document.cookie
        .split(";")
        .map((part) => part.split("=")[0]?.trim() ?? "")
        .filter((name) => name === "_ga" || name.startsWith("_ga_"));
    for (const name of names) {
        document.cookie = `${name}=; Max-Age=0; path=/`;
        if (config.cookieDomain) {
            document.cookie = `${name}=; Max-Age=0; path=/; domain=${config.cookieDomain}`;
        }
    }
}

/**
 * Record the visitor's decision and apply it on this page: accepting loads the
 * providers (or re-enables them after a withdrawal); declining after they
 * loaded opts PostHog out, disables GA4 and deletes its cookies.
 */
export function applyConsentDecision(decision: ConsentDecision, config: AnalyticsConfig | null): void {
    writeConsent(decision);
    if (!config) return;
    if (decision === "granted") {
        if (!started) {
            start(config);
            return;
        }
        analytics.posthog?.opt_in_capturing();
        setGa4Disabled(config, false);
        return;
    }
    if (!started) return;
    analytics.posthog?.opt_out_capturing();
    setGa4Disabled(config, true);
    clearGa4Cookies(config);
}

/** Lazily load the banner/dialog module; its CSS travels with it. */
function loadPrivacyControls() {
    return import("./privacy/privacyControls");
}

/**
 * Boot analytics once on app start, honouring the visitor's consent. Safe to
 * call before the rest of the app initialises — it never blocks the main
 * bundle. Call exactly once from `client/app.ts`'s entry path.
 */
export function bootstrapAnalytics(
    doc: Document = document,
    env: AnalyticsEnv = import.meta.env as AnalyticsEnv,
): void {
    const config = resolveAnalyticsConfig(env);
    const controls = {
        configured: config !== null,
        decide: (decision: ConsentDecision) => applyConsentDecision(decision, config),
    };

    // Settings → Privacy settings: always available, so the choice (and the
    // notice) can be revisited whether or not this build has analytics keys.
    doc.getElementById("privacy-settings-open")?.addEventListener("click", (event) => {
        const opener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
        void loadPrivacyControls()
            .then((m) => m.openPrivacyDialog(doc, controls, opener))
            .catch((err: unknown) => log.warn("[analytics] privacy settings failed to load", { error: err }));
    });

    if (!config) return;
    const consent = readConsent();
    if (consent === "granted") {
        start(config);
        return;
    }
    if (consent === "unset") {
        void loadPrivacyControls()
            .then((m) => m.showConsentBanner(doc, controls))
            .catch((err: unknown) => log.warn("[analytics] consent banner failed to load", { error: err }));
    }
}
