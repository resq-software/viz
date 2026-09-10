# JAXA — AW3D30 commercial-use notification

**Status: SENT 2026-09-09. ANSWERED 2026-09-10 — see
[`aw3d30-jaxa-reply.md`](./aw3d30-jaxa-reply.md).**

No longer blocking. JAXA's ALOS-2/ALOS Science Project confirmed commercial use
is permitted with no further action required, and resolved the Site Policy
question on scope: that policy governs unaltered individual images on the
website, not the AW3D30 dataset. `sendai-plain` is unblocked;
`rhine-meuse-delta` now waits only on the Copernicus 6(e) ratification.

The reply file is the evidence behind
`clauses["jaxa-commercial-use-notification"].discharged`, and both the licence
gate and the areas checker fail if it goes missing.

**To:** `earth@ml.jaxa.jp`
*(published on the page obfuscated as `earth*ml.jaxa.jp` with the instruction
"Please replace `*` with `@`")*

**Why this address:** clause 4 of the very document that creates the obligation
names it, and expressly lists *"notification of the commercial use of the Research
Data"* as a reason to write. Addressed to the Data usage policy team, Space
Technology Directorate I.

---

## What this has to do

Two things, and the second is why this is not just form-filling.

**1. Discharge clause 2.3.** The Terms of Use of Research Data say:

> The user can use the Research Data for commercial purposes without a fee;
> however, the user needs to notify JAXA thereof in advance.

That is a **notification** duty, fee-free — verified against the publisher's own
page. Sending the notice discharges it.

**2. Resolve a conflict we cannot resolve ourselves.** The same terms say the user
"is required to follow the Terms of Use on JAXA's Site Policy page" *in addition*.
That Site Policy says:

> Your use of the Materials for business or commercial purposes without the prior
> permission of the copyright holder (JAXA) is strictly prohibited.

**Notification** and **prior permission** are different obligations, and only one
of them can be discharged unilaterally.

The obvious escape — that the Site Policy governs `global.jaxa.jp` only, not
research data on `eorc.jaxa.jp` — was checked and **does not hold up**. The Site
Policy contains an extension clause reaching material on the websites of the three
former organisations merged into JAXA (NASDA, ISAS, NAL), and EORC was established
under NASDA in April 1995. So it may well reach AW3D30. Asking is the honest route.

---

## Draft

> **Subject:** Advance notification of commercial use of AW3D30 (Terms of Use of
> Research Data, clause 2.3)
>
> Dear Data usage policy team,
>
> I am writing to give the advance notification required by clause 2.3 of the
> Terms of Use of Research Data, and to ask one question about how those terms
> interact with the JAXA Site Policy.
>
> **Notification**
>
> - Organisation: ResQ Systems, Inc.
> - Dataset: ALOS World 3D-30m (AW3D30), versions 3.1 and 4.1
> - Intended use: elevation data resampled, reprojected and clipped to small
>   fixed areas, then baked into terrain used by a commercial 3D simulation
>   product for emergency-response planning and training. The data are not
>   redistributed in their original form.
> - Areas: initially a 4 km square over the Sendai coastal plain, Japan (v3.1),
>   and a 4 km square over the Rhine–Meuse delta, Netherlands (v4.1).
> - Attribution: we carry the credit required by clause 2.1 in the product's
>   third-party notices — "The original data used for this product have been
>   supplied by JAXA's ALOS World 3D-30m (AW3D30)."
>
> **Question**
>
> The Terms of Use of Research Data state that, in addition to those terms, the
> user is required to follow the Terms of Use on the JAXA Site Policy page. The
> Site Policy states that use of the Materials for business or commercial
> purposes without JAXA's prior permission is strictly prohibited, whereas clause
> 2.3 of the Research Data terms permits commercial use without a fee subject to
> advance notification.
>
> Could you confirm which applies to AW3D30 — whether this notification is
> sufficient, or whether we also require prior written permission? If the latter,
> please treat this message as that request and tell us what further information
> you need.
>
> I would be grateful for a written reply we can retain as a record of compliance.
>
> With thanks,
> [name, title]
> ResQ Systems, Inc.

---

## When a reply arrives

Record it against `jaxa-commercial-use-notification` in
`tools/licences/licences.json`: set `needs_drafting` to `false`, put the substance
of JAXA's answer in `text`, and note the date and correspondent. That is what
unblocks the two areas — the clause is deliberately unsatisfiable until then.

**Do not mark it discharged on the strength of having sent the email.** If JAXA
confirms prior permission is required, sending a notification discharged nothing,
and the clause should stay blocking until permission is actually given.
