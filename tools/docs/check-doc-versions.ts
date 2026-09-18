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

// Checks that every dependency version NAMED IN PROSE matches the one package.json declares.
//
// Written because the three.js 0.185 → 0.186 bump left three documents naming the old version:
// a README badge, a README paragraph and the line in CLAUDE.md that every agent reads first.
// One was fixed by hand, a reviewer caught the second, and the third only turned up on a sweep.
// A version printed in a document is a claim about the build, and until now nothing evaluated
// it — so the documentation drifted while every gate stayed green.
//
// Usage:
//   node --experimental-strip-types tools/docs/check-doc-versions.ts [--claims P] [--root P]

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";

/** One prose claim about a dependency's version. */
interface VersionClaim {
  /** Repo-relative document naming the version. */
  readonly file: string;
  /** package.json key the claim is about. */
  readonly package: string;
  /** package.json declaring that dependency, repo-relative. */
  readonly manifest: string;
  /**
   * Regex with exactly one capture group: the version as the document writes it.
   *
   * Matched with the `g` flag, and EVERY match is checked. A document that names the version
   * twice — as README did — must not be able to satisfy this by getting one of them right.
   */
  readonly pattern: string;
  /** Why this claim exists, printed on failure so the fix is obvious. */
  readonly note: string;
}

interface Problem {
  readonly code: string;
  readonly file: string;
  readonly message: string;
}

const args = process.argv.slice(2);
const flag = (name: string, fallback: string): string => {
  const i = args.indexOf(name);
  return i >= 0 && i + 1 < args.length ? args[i + 1]! : fallback;
};

const ROOT = resolve(flag("--root", resolve(dirname(process.argv[1]!), "..", "..")));
const CLAIMS_PATH = resolve(flag("--claims", resolve(ROOT, "tools/docs/version-claims.json")));

/** Strips a semver range operator, leaving the version it pins from. */
export function declaredVersion(range: string): string | null {
  const m = /^[\^~>=<\s]*([0-9]+(?:\.[0-9]+)*)/.exec(range);
  return m ? m[1]! : null;
}

/**
 * True when a documented version agrees with the declared one, at the precision it was written.
 *
 * Prose abbreviates — CLAUDE.md says "Three.js 0.186" where package.json says "^0.186.0" — so a
 * documented version is compared component-wise as a PREFIX. That accepts a legitimately shorter
 * form while still rejecting 0.185 against 0.186.0, which is the whole point. It deliberately
 * does NOT accept a longer documented version than the declared one: "0.186.2" is a claim
 * package.json does not support.
 */
export function agrees(documented: string, declared: string): boolean {
  const d = documented.split(".");
  const p = declared.split(".");
  if (d.length > p.length) return false;
  return d.every((part, i) => part === p[i]);
}

function readJson(path: string): unknown {
  if (!existsSync(path)) throw new Error(`missing file: ${path}`);
  return JSON.parse(readFileSync(path, "utf8"));
}

/** Looks a dependency up across both dependency blocks. */
export function versionOf(manifest: unknown, name: string): string | null {
  if (typeof manifest !== "object" || manifest === null) return null;
  const m = manifest as Record<string, unknown>;
  for (const block of ["dependencies", "devDependencies"]) {
    const deps = m[block];
    if (typeof deps === "object" && deps !== null) {
      const found = (deps as Record<string, unknown>)[name];
      if (typeof found === "string") return found;
    }
  }
  return null;
}

/** Checks one claim, returning every problem it raises. */
export function checkClaim(
  claim: VersionClaim,
  document: string,
  manifest: unknown,
): Problem[] {
  const problems: Problem[] = [];

  const range = versionOf(manifest, claim.package);
  if (range === null) {
    // The package was renamed or removed and the claim was left behind. Silently passing here
    // would retire the check without retiring the claim.
    problems.push({
      code: "package-not-declared",
      file: claim.file,
      message: `'${claim.package}' is not in ${claim.manifest}; the claim names a package this build does not have.`,
    });
    return problems;
  }

  const declared = declaredVersion(range);
  if (declared === null) {
    problems.push({
      code: "unparsable-range",
      file: claim.file,
      message: `'${claim.package}' is declared as '${range}' in ${claim.manifest}, which names no version.`,
    });
    return problems;
  }

  const matches = [...document.matchAll(new RegExp(claim.pattern, "g"))];

  if (matches.length === 0) {
    // A guard that can silently find nothing to check is not a guard. The document was
    // rewritten and the pattern no longer reaches the version it was written to police.
    problems.push({
      code: "claim-matches-nothing",
      file: claim.file,
      message:
        `pattern /${claim.pattern}/ matched nothing. Either the version is no longer named ` +
        `there — in which case delete the claim — or the wording moved and this check has ` +
        `stopped checking anything. (${claim.note})`,
    });
    return problems;
  }

  for (const match of matches) {
    const documented = match[1];
    if (documented === undefined) {
      problems.push({
        code: "pattern-has-no-capture",
        file: claim.file,
        message: `pattern /${claim.pattern}/ matched but captured no version; it needs exactly one capture group.`,
      });
      continue;
    }

    if (!agrees(documented, declared)) {
      problems.push({
        code: "documented-version-drifted",
        file: claim.file,
        message:
          `says '${claim.package}' is ${documented}, but ${claim.manifest} declares ` +
          `${range} (${declared}). ${claim.note}`,
      });
    }
  }

  return problems;
}

/**
 * Checks that the commit CLAUDE.md names for a submodule is the commit actually checked out.
 *
 * CLAUDE.md states the `lib/dotnet-sdk` pin as a short SHA and explains at length why it is that
 * commit and not a tag, so it reads as authoritative. Nothing verified it: bumping the pin for
 * dotnet-sdk#101 left the document naming the superseded commit, and the only thing that caught
 * it was remembering to look. A stated SHA is a claim about the build exactly as a stated
 * version is.
 *
 * Read from the git index rather than from `lib/dotnet-sdk/.git`, so it is correct in a fresh
 * clone where the submodule has not been initialised.
 *
 * @param root Repository root.
 * @returns Problems found; empty when the document agrees.
 */
function checkSubmodulePins(root: string): Problem[] {
  const doc = "CLAUDE.md";
  const path = "lib/dotnet-sdk";
  const docPath = resolve(root, doc);
  if (!existsSync(docPath)) return [];

  let actual: string;
  try {
    // The INDEX, not HEAD: it reflects what is about to be committed, so a bump and its
    // documentation can be staged together without the check failing in between. In CI, where
    // the tree is a fresh checkout, the two are identical.
    const out = execFileSync("git", ["ls-files", "-s", path], { cwd: root, encoding: "utf8" });
    const m = /^160000\s+([0-9a-f]{40})\s/.exec(out);
    if (!m) {
      return [{ code: "submodule-not-in-index", file: doc,
                message: `git ls-files reported no gitlink for '${path}'.` }];
    }
    actual = m[1]!;
  } catch {
    // Silence here is the failure mode this whole file exists to avoid.
    return [{ code: "submodule-unreadable", file: doc,
              message: `could not read the '${path}' pin from the git index.` }];
  }

  const stated = [...readFileSync(docPath, "utf8")
    .matchAll(/pinned to \*\*`([0-9a-f]{7,40})`/g)].map(m => m[1]!);

  if (stated.length === 0) {
    return [{ code: "claim-matches-nothing", file: doc,
              message: "no submodule pin is stated; the wording moved or the section went." }];
  }

  return stated.filter(sha => !actual.startsWith(sha)).map(sha => ({
    code: "submodule-pin-drifted",
    file: doc,
    message: `says the ${path} pin is ${sha}, but the index has ${actual.slice(0, sha.length)}.`,
  }));
}

function main(): void {
  const claims = readJson(CLAIMS_PATH);
  if (!Array.isArray(claims)) {
    console.error(`${CLAIMS_PATH} must contain an array of claims.`);
    process.exit(1);
  }

  if (claims.length === 0) {
    // An empty registry passes every check while checking nothing.
    console.error(`${CLAIMS_PATH} declares no claims; the check would pass vacuously.`);
    process.exit(1);
  }

  const problems: Problem[] = [];
  const manifests = new Map<string, unknown>();

  for (const raw of claims) {
    const claim = raw as VersionClaim;
    const docPath = resolve(ROOT, claim.file);
    const manifestPath = resolve(ROOT, claim.manifest);

    if (!existsSync(docPath)) {
      problems.push({
        code: "document-missing",
        file: claim.file,
        message: `claim points at a file that does not exist.`,
      });
      continue;
    }

    if (!manifests.has(manifestPath)) manifests.set(manifestPath, readJson(manifestPath));

    problems.push(
      ...checkClaim(claim, readFileSync(docPath, "utf8"), manifests.get(manifestPath)),
    );
  }

  problems.push(...checkSubmodulePins(ROOT));

  if (problems.length) {
    console.error("Documented claims that do not match the build:");
    for (const p of problems) console.error(`  ERROR  ${p.code}  ${p.file}: ${p.message}`);
    console.error(`\n${problems.length} problem(s). Doc version check FAILED.`);
    process.exit(1);
  }

  console.log(`Doc version check passed (${claims.length} claim(s)).`);
}

// Only run when invoked directly, so the test file can import the pure helpers above.
if (process.argv[1] && resolve(process.argv[1]).endsWith("check-doc-versions.ts")) main();
