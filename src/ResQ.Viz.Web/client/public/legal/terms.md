# End-user terms — DRAFT, NOT IN FORCE

> **This document is not binding and must not be shipped as though it were.**
>
> It is a skeleton, written so that obligations this product has already incurred
> have somewhere to live, and so counsel reviews a draft rather than a blank page.
> Every section marked **[COUNSEL]** is a commercial or legal decision nobody here
> is qualified to make.
>
> Until a named reviewer has approved it, `copernicus-6e-flowdown` in
> `tools/licences/licences.json` stays unratified and the licence gate refuses any
> tile that depends on it. That is deliberate.

---

## Why this file exists

Four obligations attach to data baked into this product, and none is satisfied by
a document nobody reads:

| obligation | source | discharged in |
|---|---|---|
| Liability sentence, verbatim | Copernicus DEM, Art. 6(c) | `notices.md`, generated |
| Not for navigation | NOAA CUDEM/CRM, GMRT, EMODnet | `notices.md`, generated |
| Modification disclosure | USGS 3DEP use constraints | `notices.md`, generated |
| **Flow-down to subsequent users** | **Copernicus DEM, Art. 6(e)** | **here — §4** |

The first three are *statements*: carrying the text discharges them, and
`notices.md` is generated from the licence register so they cannot drift. The
fourth is a *contract term* — it binds whoever receives the data from you — and a
statement cannot do that job.

**Read [`notices.md`](./notices.md) alongside this.** It is generated from the
provenance manifest and is the authoritative list of what this product contains
and what each source requires.

---

## 1. Scope

**[COUNSEL]** — who the licensee is, what is licensed (the software, the baked
terrain data, or both), term, territory, and how the licence ends.

## 2. Grant

**[COUNSEL]** — what the licensee may do.

One question decides whether §4 is needed at all, so answer it first:

> **Does the licensee receive any right to distribute or communicate the baked
> elevation data onward, to anyone?**

If **no** — the grant is to use the product, not to redistribute its data — then
Copernicus Article 6(e) is not engaged, because it applies only "where the user
grants to any Subsequent User the rights to distribute or communicate to the
General Public". §4 can then be deleted and an express *no-redistribution*
restriction put in its place, which is simpler and cheaper to comply with.

If **yes**, §4 is mandatory and must survive review intact.

## 3. Restrictions

**[COUNSEL]** — reverse engineering, benchmarking, export control, and whether
redistribution of the baked data is prohibited outright (see §2).

**Not a drafting matter, and not to be softened:** this product's terrain and
bathymetry are **not suitable for navigation**, and no term here may imply
otherwise. The wording is in `notices.md`.

## 4. Copernicus flow-down — [COUNSEL, and not optional if §2 grants redistribution]

Drafted from the verbatim Article 6 of the Copernicus DEM licence, and reproduced
from `copernicus-6e-flowdown` in the licence register so the two cannot drift.
**Change it there, not here.**

> Where this product supplies elevation data derived from the Copernicus
> WorldDEM-30, and where you are granted any right to distribute or communicate
> that data to the public, whether modified or not, you must ensure that anyone
> receiving it from you is bound by these same obligations, namely: (a) to state,
> where the data have been adapted or modified, "produced using Copernicus
> WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018
> provided under COPERNICUS by the European Union and ESA; all rights reserved";
> (b) to ensure that recipients understand that neither the Licensor nor any other
> entity in charge of the Copernicus programme may be held liable in any respect,
> and to include in their own licence, warning or notice the sentence "The
> organisations in charge of the Copernicus programme by law or by delegation do
> not incur any liability for any use of the Copernicus WorldDEM-30"; (c) not to
> state or imply that the Licensor or the Copernicus programme endorses them,
> their use of the data, or any product they make from it; and (d) to impose these
> same obligations, including this one, on anyone to whom they in turn grant such
> rights.

Three things a reviewer should check, because each shaped the wording:

1. **"The above obligations" means 6(a) through 6(d)**, not just attribution. The
   draft flows down the modified-data notice, the liability sentence, the
   non-endorsement duty, and 6(e) itself so the chain does not break at the second
   hop. 6(d) is the one most easily dropped.
2. **The two quoted strings are the licence's own wording** and carry authority.
   Everything around them is ours and carries none until approved.
3. **6(c) binds this product regardless of §2.** Unlike 6(e) it is unconditional,
   because this product does communicate to the general public. It is discharged
   in `notices.md`, not here.

## 5. Data accuracy and fitness

**[COUNSEL]** — the warranty position.

Factual input from the design record rather than assumption: terrain outside US
territory derives from **surface** models, so below the tree line the modelled
ground is canopy top rather than earth, and the product declines to give a
confident mobility verdict there. Elevation and bathymetry are resampled and
reprojected. This is a planning and training tool.

## 6. Third-party notices

The notices at [`notices.md`](./notices.md) form part of these terms. They are
generated from the provenance manifest by the licence gate, and the build refuses
to produce a release without them.

## 7. Liability, indemnity, governing law, changes to these terms

**[COUNSEL]** — all of it.

---

## Before this ships

1. **Answer the §2 redistribution question.** It may delete §4 entirely.
2. **Have counsel review**, then record the reviewer and date in
   `clauses["copernicus-6e-flowdown"].ratified` in `tools/licences/licences.json`.
   The gate keeps refusing Copernicus-derived tiles until that exists.
3. **Decide how these terms are presented.** A link in the settings panel makes
   them findable, which is enough to *read* them and probably not enough to *form
   a contract*. That is a product and legal decision, not an engineering one.
