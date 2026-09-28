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

## EU Cyber Resilience Act reporting

Where the EU Cyber Resilience Act (Regulation (EU) 2024/2847) applies to a product in this repository, ResQ reports through ENISA's Single Reporting Platform, to the CSIRT designated as coordinator and to ENISA:

- **Actively exploited vulnerabilities:** an early warning within 24 hours of becoming aware of one, a notification within 72 hours, and a final report no later than 14 days after a corrective or mitigating measure is available.
- **Severe incidents** affecting the security of the product: an early warning within 24 hours, a notification within 72 hours, and a final report within one month of the notification.

We inform affected users as the Act requires. Reporting to us through the private channels above never requires you to contact ENISA yourself. You may also report to a national CSIRT under its coordinated vulnerability disclosure policy.

## Safe harbour

If you make a good-faith effort to follow this policy while researching a vulnerability, we will:

- treat your research as authorised, and not pursue or support legal action against you for it;
- work with you to understand and fix the issue quickly; and
- credit you, unless you ask us not to.

Good faith means that you:

- test only against your own accounts, data and installations;
- stop and report as soon as you find a vulnerability;
- access, change or keep no one else's data beyond what's needed to show the issue;
- don't degrade our services or anyone else's; and
- give us reasonable time to fix the issue before you disclose it.

This safe harbour covers ResQ's own claims only; it cannot bind third parties.

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
