// Copyright 2024 ResQ Technologies Ltd.
// Licensed under the Apache License, Version 2.0
// (see https://www.apache.org/licenses/LICENSE-2.0)

namespace ResQ.Viz.Web;

/// <summary>
/// Single source of truth for security-header literals that need to stay in
/// lockstep between the middleware that emits them and the integration tests
/// that regression-guard them.
/// </summary>
/// <remarks>
/// Currently scoped to CSP source-list contents that are most prone to drift —
/// namely the SHA-256 hashes of inline scripts that Cloudflare Web Analytics
/// auto-injects. Cloudflare rotates the bootstrap script content periodically,
/// producing a new hash each time; centralising the list here means a hash
/// rotation is a single edit instead of a risky multi-file find/replace.
///
/// Disabling Cloudflare Web Analytics auto-inject in the dashboard removes
/// the need for these hashes entirely — GA4 + PostHog already cover RUM and
/// product analytics — at which point this class can be deleted.
/// </remarks>
public static class SecurityConstants
{
    /// <summary>
    /// SHA-256 hashes of the Cloudflare Web Analytics bootstrap inline script,
    /// formatted as CSP source-list tokens (single-quoted, including the
    /// <c>sha256-</c> prefix). Each variant Cloudflare has rotated through in
    /// production must remain present until the auto-inject is disabled.
    /// </summary>
    public static readonly IReadOnlyList<string> CloudflareBeaconScriptHashes =
    [
        "'sha256-ZlBaXTgBboiytLHGbGnTgT67kpRdxavJqMHVBSTxRaE='",
        "'sha256-XtmvLUr10hivmkrNKCgQcNREHptkg6tWqm9iZo3mlAc='",
        "'sha256-W6TA06UC0UF2YUyVLqmuSLDKr+EdUQmWuJVAeE7inQY='",
        "'sha256-wzOq9WwKOqEF6KuUWlfNH+0sSsKDE6zLRou/Z2YSSZE='",
    ];
}
