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
>
> **It also lives here, outside the shipped assets, on purpose.** The first draft
> of this was placed in `client/public/legal/` and linked from the settings panel
> as "Terms" — a document headed *not in force*, with liability and warranty left
> as placeholders, presented to users as the terms. Review caught it. When counsel
> approves it, move it to `client/public/legal/terms.md` and restore the link in
> `client/index.html`; `scripts/copy-legal.mjs` refuses to build while anything
> other than the generated notices sits in that directory, so the move and the
> approval have to happen together.

---

## Why this file exists

Seven obligations attach to data baked into this product, and none is satisfied by
a document nobody reads:

| obligation | source | binds this product | discharged in |
|---|---|---|---|
| Source notice on any distribution or communication | Copernicus DEM, Art. 6(a) | yes — the product communicates to the public | `notices.md`, generated |
| Modified-data notice | Copernicus DEM, Art. 6(b) | yes — the bake resamples and reprojects | `notices.md`, generated |
| Liability sentence, verbatim | Copernicus DEM, Art. 6(c) | yes | `notices.md`, generated |
| No implied endorsement | Copernicus DEM, Art. 6(d) | yes, unconditionally | `notices.md`, generated |
| **Flow-down to subsequent users** | **Copernicus DEM, Art. 6(e)** | **only if §2 grants redistribution** | **here — §4** |
| Not for navigation | NOAA CUDEM/CRM, GMRT, EMODnet | yes | `notices.md`, generated |
| Modification disclosure | USGS 3DEP use constraints | yes | `notices.md`, generated |

Every one of these except 6(e) is a *statement*: carrying the text discharges it,
and `notices.md` is generated from the licence register so they cannot drift.
6(e) is a *contract term* — it binds whoever receives the data from you — and a
statement cannot do that job.

One row is worth a second look. **6(a) and 6(b) are discharged by a single
string.** The notice the register carries is 6(b)'s modified-data form, which
reproduces 6(a)'s source notice word for word inside itself behind the prefix
*produced using Copernicus WorldDEM-30*. That is a reading, not a certainty;
a reviewer should confirm it rather than take it on trust.

**Read [`NOTICE.md`](../../NOTICE.md) alongside this.** It is generated from the
provenance manifest and is the authoritative list of what this product contains
and what each source requires. `scripts/copy-legal.mjs` copies it to
`client/public/legal/notices.md` at build time, which is the name §6 refers to and
the name users see.

---

## 1. Scope

**[COUNSEL]** — who the licensee is, what is licensed (the software, the baked
terrain data, or both), term, territory, and how the licence ends.

## 2. Grant

**ANSWERED 2026-09-10 — no redistribution.** The commercial decision is taken: a
licensee receives the right to *use* the product, not to distribute or communicate
its baked elevation data to anyone. §3 carries that as an express restriction.

The question this settles, kept because the reasoning still has to survive counsel:

> **Does the licensee receive any right to distribute or communicate the baked
> elevation data onward, to anyone?**

**No.** So Copernicus Article 6(e) is not engaged — it applies only "where the user
grants to any Subsequent User the rights to distribute or communicate to the
General Public", and this product grants no such right.

Two facts that shaped it, neither of which decided it:

- **Nothing upstream forced the answer.** The Copernicus licence's Right of Use
  article already grants reproduction, distribution, communication to the general
  public, and adaptation — worldwide, unlimited in time, free of charge under the
  Financial Conditions article. Whether the *licensee* gets any of that was purely
  this product's commercial call.
- **Breach of Article 6 ends the licence.** The Termination article — cite it by
  title, because the licence numbers it "Article 9" and numbers the IPR article
  immediately before it "Article 9" too — lets the Licensor terminate with the
  immediate result of the user losing every right granted. Granting redistribution
  would have bought a clause that has to survive every future edit, for a
  planning-and-training tool that has no product reason to redistribute terrain.

**What this does NOT settle, and what has not changed:**

1. **6(d) still binds, unconditionally.** It is the one Article 6 obligation with no
   trigger — no distribution condition, no modification condition — so answering §2
   "no" does not clear Article 6. It is carried in `notices.md` as
   `copernicus-6d-nonendorsement`, and 6(a)–(c) are carried there too because this
   product itself communicates to the general public.
2. **The licence gate is unchanged and still blocking.**
   `clauses["copernicus-6e-flowdown"]` remains `ratified: null`, so every
   Copernicus-derived tile is still refused. That is deliberate. A commercial
   decision that 6(e) is not engaged is not the same thing as counsel confirming it,
   and the register is built so the second has to happen explicitly. Clearing it is
   the reviewer's act, not this document's.
3. **If §2 is ever reopened**, §4 comes back with it. It is kept below rather than
   deleted for that reason, and because it is the record of what the obligation
   actually says.

## 3. Restrictions

**[COUNSEL]** — reverse engineering, benchmarking, export control.

**No redistribution of the baked data.** Settled by §2, and the term that replaces
§4 rather than sitting beside it. Counsel drafts the wording; the substance is that
a licensee may use the product and may not distribute, publish, or communicate to
the public the elevation, bathymetry or land-cover data it contains, in whole or in
part, whether modified or not.

Two notes for whoever drafts it:

- **Scope it to the DATA, not to output.** A screenshot, a printed map, a recorded
  fly-through — a planning and training tool is useless if its pictures cannot leave
  the building. The restriction that Article 6(e) turns on is the onward supply of
  the elevation data itself as data.
- **Scenario sharing is the case to check.** If two agencies ever need to exchange a
  prepared scenario, share it by *area id* and let the recipient's install obtain
  the same tiles under its own licence. Embedding terrain in a shared scenario is
  redistribution and would reopen §2. The codebase is already shaped this way —
  scenarios reference terrain by preset id rather than carrying it — so this costs
  nothing today and is worth not losing.

**Not a drafting matter, and not to be softened:** this product's terrain and
bathymetry are **not suitable for navigation**, and no term here may imply
otherwise. The wording is in `notices.md`.

## 4. Copernicus flow-down — NOT IN FORCE while §2 grants no redistribution

**§2 was answered "no" on 2026-09-10, so Article 6(e) is not engaged and this section does
not go into the shipped terms.** It is kept for two reasons: it is the record of what 6(e)
actually obliges, checked against the licence itself, and it comes straight back if §2 is ever
reopened. The register still requires ratification before any Copernicus-derived tile can bake —
see §2, note 2.

Verified against the licence for Copernicus DEM instance **COP-DEM-GLO-30-F**,
vendored at `tools/licences/texts/copernicus-worlddem-30.txt` and hashed into the
register, because the publisher's own directory URL now returns 403.

The text below is **generated from `copernicus-6e-flowdown` in the licence
register**. Change it there, not here; a test in `check-licences.test.ts` fails if
the two stop matching.

<!-- BEGIN copernicus-6e-flowdown — generated from tools/licences/licences.json -->
> Part of the elevation data in this product derives from the Copernicus WorldDEM-30, supplied under the licence for Copernicus DEM instance COP-DEM-GLO-30-F. Article 6 of that licence places obligations on every user of that data. The following obligations bind you directly.
>
> (a) When you communicate the data to the general public or distribute it, whether or not you have modified it, you must inform the general public of the source by using the notice: "© DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved."
>
> (b) Where you have adapted or modified the data, you must in addition provide the notice: "produced using Copernicus WorldDEM-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved".
>
> (c) If you exercise a right to distribute the data or to communicate it to the general public, modified or not, you must ensure that those who receive it from you understand that neither the Licensor nor any other legal entity in charge of the Copernicus programme, or of the delivery of Copernicus data and information under that programme, may be held liable with regard to any aspect of the Copernicus WorldDEM-30; and you must add to your own licence, or to any legal warning or notice covering that distribution or communication, the sentence: "The organisations in charge of the Copernicus programme by law or by delegation do not incur any liability for any use of the Copernicus WorldDEM-30".
>
> (d) You must not convey the impression to the general public that your activities are officially endorsed by the Provider, the Licensor, or any other legal entity in charge of the Copernicus programme or of the delivery of Copernicus data and information under that programme. This obligation applies whether or not you ever distribute the data.
>
> (e) Where you grant anyone else the right to distribute the data or to communicate it to the general public, modified or not, you must ensure that they are bound by obligations (a) to (d) above and by this obligation (e), so that every subsequent recipient carries the same duties.
<!-- END copernicus-6e-flowdown -->

Five things a reviewer should check, because each shaped the wording:

1. **The lettering is the licence's own.** (a)–(e) here are Article 6(a)–(e)
   there, so this can be diffed against the licence line by line. An earlier draft
   renumbered — its (a)–(d) were 6(b)–(e) — and dropped 6(a) in the process.
2. **"The above obligations" in 6(e) means 6(a) through 6(d)**, not just
   attribution. The clause carries all four and then re-imposes itself as (e), so
   the chain does not break at the second hop. 6(d) is the one most easily lost.
3. **The triggers differ per sub-letter, and 6(d) has none.** 6(a) applies on any
   distribution or communication, modified or not; 6(b) only where the data have
   been adapted; 6(c) where a distribution or communication right is exercised;
   6(d) unconditionally. So 6(d) binds this product whatever §2 decides, and is
   carried in `notices.md` as `copernicus-6d-nonendorsement`. It is the reason
   answering §2 "no" does not clear Article 6 entirely.
4. **The licensee is bound directly, not merely asked to pass obligations on.** A
   licensee who distributes is a User under Article 6 in their own right. An
   earlier draft obliged them only to bind the *next* recipient, which left the
   licensee's own conduct unaddressed — the gap that lettering fix exposed.
5. **The three quoted strings are the licence's own wording** and carry authority;
   they are reproduced with the real copyright symbol because the licence mandates
   them verbatim. Everything around them is ours and carries none until approved.

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

> **Do not "fix" that link.** It is relative to `client/public/legal/`, where this
> document goes once approved and where `notices.md` already sits. It does not
> resolve from `tools/licences/`, and that is the only thing about this file that
> is supposed to be broken while it waits here. §6 is terms text addressed to a
> user; the link on line 51 is scaffolding addressed to you.

## 7. Liability, indemnity, governing law, changes to these terms

**[COUNSEL]** — all of it.

---

## Before this ships

1. ~~**Answer the §2 redistribution question.**~~ **Done 2026-09-10: no
   redistribution.** §4 is out of the shipped terms; §3 carries the express
   restriction instead. The licence gate is deliberately unchanged — see §2, note 2.
2. **Have counsel review**, then record the reviewer and date in
   `clauses["copernicus-6e-flowdown"].ratified` in `tools/licences/licences.json`.
   The gate keeps refusing Copernicus-derived tiles until that exists.
3. **Re-check the two `notices.md` links after the move.** §6's becomes correct the
   moment this file lands in `client/public/legal/`; the one above it stops being
   correct at the same moment and needs repointing.
4. **Decide how these terms are presented.** A link in the settings panel makes
   them findable, which is enough to *read* them and probably not enough to *form
   a contract*. That is a product and legal decision, not an engineering one.
