# USDA — NAIP terms of use

**Status:** ready to send once the acquisition years are filled in. Unblocks
`paradise-ca`, the only area using NAIP.

**To:** `geo.sales@usda.gov` — USDA Farm Production and Conservation Business
Center (FPAC-BC), Geospatial Enterprise Operations (GEO), Customer Services.
Phone 801-844-2922. Postal: 125 South State Street, Suite 6416, Salt Lake City,
UT 84138.

**Do not use the old addresses.** `apfo.sales@slc.usda.gov` and the 2222 W 2300
South address appear throughout older NAIP information sheets and are both
superseded — FPAC-BC GEO is the successor to the FSA Aerial Photography Field
Office. Confirmed against three independent USDA sources including the May 2025
customer information sheet.

**There is no published procedure for a rights question.** Every documented
FPAC-BC GEO procedure is for *ordering* imagery. The only general channel is the
customer services address above. Expect to be routed onward; write so the first
recipient can forward it without having to ask what you meant.

---

## Why this is not just "find the licence page"

**There is no NAIP licence document.** Not a missing link — it does not exist.
FSA, FPAC-BC, NRCS, data.gov, the USDA NAL GeoNetwork, USDA's own ArcGIS Online
items, the NAIP Hub, and every NAIP information sheet from 2011–2017 plus the 2025
customer sheet were checked. No USDA page states NAIP's terms of use as such. The
public-domain position rests on one internal agency notice and on constraint
fields in per-tile FGDC metadata.

**And there is a live question underneath it.** FSA Notice **AP-26** (27 Nov 2017)
states affirmatively that NAIP imagery "has been placed in public domain" — note
that is an *act of placement*, not a claim that it is uncopyrightable by operation
of law. That matters, because NAIP is contractor-acquired and the public-domain
chain depends on the Government actually taking copyright from the contractor
under a clause like H-3.

AP-26 exists **because FSA was surveying State Offices to determine "the minimum
data rights required under a licensed data model"** — commercial-off-the-shelf
imagery carrying a EULA. The outcome of that survey could not be established from
any USDA source. AP-27, AP-28 and AP-29 exist; AP-30 onward return 404.

So:

- The newest NAIP contract whose copyright-transfer clause could be verified is
  the **2012** cycle. The contract governing current acquisitions could not be
  obtained.
- **Nothing found establishes that the public-domain model continued past 2017** —
  and 2018-onward is exactly the 0.6 m / 0.3 m era a modern product would want.
- No USDA statement addresses **downstream commercial redistribution** by a third
  party at all.
- Attribution is phrased as a *request* in every USDA source ("asks to be
  credited"). AWS Open Data's "Public Domain with Attribution" is a
  redistributor's characterisation, not USDA's wording.

That is why this letter asks about specific acquisition years rather than about
NAIP in the abstract.

---

## Draft

> **Subject:** NAIP imagery — terms of use and data rights for commercial
> redistribution
>
> Dear FPAC-BC Geospatial Enterprise Operations,
>
> I am writing with a data-rights question about NAIP imagery. I appreciate this
> is not an ordering enquiry; if another office is better placed to answer, I
> would be grateful if you could forward it.
>
> We are a US company building a commercial 3D simulation product used for
> emergency-response planning and training. We would like to include NAIP imagery,
> resampled and clipped to small fixed areas and baked into the product's terrain.
> The imagery would ship inside a commercial product; it would not be
> redistributed in its original form or offered as an imagery service.
>
> We have not been able to locate a USDA-published statement of NAIP's terms of
> use. The public-domain position appears to rest on FSA Notice AP-26 (27 November
> 2017) and on the access and use constraints in the per-tile FGDC metadata. We
> would rather ask than infer.
>
> Could you confirm, in writing:
>
> 1. Whether NAIP imagery for the following acquisitions is in the public domain
>    and free of any licence restriction on commercial redistribution as part of a
>    derived product:
>    - [state, county or bounding area]
>    - [acquisition year(s)]
> 2. Whether the acquisition model described in FSA Notice AP-26 — under which
>    imagery was placed in the public domain — remained in effect for those years,
>    or whether any of them were acquired under a licensed / commercial-off-the-
>    shelf model carrying end-user licence terms.
> 3. Whether crediting USDA in derived products is a requirement or a request. The
>    FGDC metadata says USDA "asks to be credited"; we are glad to credit either
>    way and want to describe the obligation accurately in our own notices.
> 4. Whether any published USDA document states NAIP's terms of use, which we
>    could cite rather than relying on an internal notice.
>
> If it is easier to answer for a single acquisition, the one we need first is
> [state / county], [year].
>
> With thanks for your time,
> [name, title]
> ResQ Systems, Inc.

---

## Before sending

**Fill in the acquisition years and states.** Question 2 is the one that matters
and cannot be answered in the abstract — the risk is specific to when and where
the imagery was flown. Check which NAIP acquisitions the Paradise, CA area would
draw on and name them.

## When a reply arrives

Update the `naip` entry in `tools/licences/licences.json`. Set `verified_on` only
if USDA's answer actually establishes the terms; if the reply is equivocal, leave
it null and record what they said. An equivocal answer written up as a clearance
would be worse than the current honest block.

If USDA confirms any relevant acquisition was made under a licensed model, that is
not a small correction — it would mean NAIP is not admissible under this
registry's allowed classes at all, and the area needs a different imagery source
or none.
