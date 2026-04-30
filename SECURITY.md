<!--
  Copyright 2026 ResQ Systems, Inc.
  SPDX-License-Identifier: Apache-2.0
-->

# Security Policy

## Reporting a Vulnerability

If you discover a security issue in **ResQ Viz**, please report it privately. Do **not** open a public GitHub issue.

**Preferred — GitHub private vulnerability reporting**

Open a private security advisory: <https://github.com/resq-software/viz/security/advisories/new>

**Alternative — email**

If you cannot use GitHub advisories, email **security@resq.software** with the details below.

Please include:

- A description of the vulnerability and its impact
- Reproduction steps or proof-of-concept
- Affected commit SHA, tag, or deployment
- Any suggested mitigation

We aim to acknowledge reports within **3 business days** and provide an initial assessment within **7 days**.

## Scope

| In scope | Out of scope |
|---|---|
| ASP.NET Core host (`src/ResQ.Viz.Web/`) | Bundled SDK in the `lib/dotnet-sdk` submodule — report at <https://github.com/resq-software/dotnet-sdk> |
| TypeScript frontend (`src/ResQ.Viz.Web/client/`) | Third-party dependencies — report upstream |
| CI/CD configuration in `.github/` | Demo content / synthetic scenario data |
| Static assets served from src/ResQ.Viz.Web/wwwroot/ |  |

## Supported Versions

This project does not yet have a stable release. Security fixes are applied to the `main` branch and shipped via the next deploy of <https://viz.resq.software>.

## Acknowledgements

Thank you to the security community for helping keep the ResQ ecosystem safe. Reporters who follow this policy in good faith will be credited in the relevant security advisory unless they request otherwise.
