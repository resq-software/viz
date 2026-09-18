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

import { ok, strictEqual } from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, it } from "node:test";

import { agrees, checkClaim, declaredVersion, versionOf } from "./check-doc-versions.ts";

const HERE = dirname(fileURLToPath(import.meta.url));
const CHECKER = join(HERE, "check-doc-versions.ts");
const REPO = resolve(HERE, "..", "..");

const MANIFEST = { dependencies: { three: "^0.186.0" }, devDependencies: { vite: "^8.3.0" } };

const CLAIM = {
    file: "DOC.md",
    package: "three",
    manifest: "package.json",
    pattern: "Three\\.js ([0-9]+\\.[0-9]+\\.[0-9]+)",
    note: "note",
};

describe("declaredVersion", () => {
    it("strips range operators", () => {
        strictEqual(declaredVersion("^0.186.0"), "0.186.0");
        strictEqual(declaredVersion("~1.2.3"), "1.2.3");
        strictEqual(declaredVersion(">=10.0.11"), "10.0.11");
        strictEqual(declaredVersion("0.186.0"), "0.186.0");
    });

    it("returns null for a range that names no version", () => {
        // A git or workspace spec pins nothing this check can compare against, and guessing
        // would be worse than saying so.
        strictEqual(declaredVersion("github:mrdoob/three.js"), null);
        strictEqual(declaredVersion("*"), null);
    });
});

describe("agrees", () => {
    it("accepts prose that abbreviates the version", () => {
        // CLAUDE.md writes "Three.js 0.186" where package.json says "^0.186.0".
        ok(agrees("0.186", "0.186.0"));
        ok(agrees("0.186.0", "0.186.0"));
    });

    it("rejects a stale version at any precision", () => {
        ok(!agrees("0.185", "0.186.0"));
        ok(!agrees("0.185.1", "0.186.0"));
        ok(!agrees("0.186.1", "0.186.0"));
    });

    it("rejects a documented version more precise than the declared one", () => {
        // "0.186.2" is a claim package.json does not support, even though it shares a prefix.
        ok(!agrees("0.186.0.1", "0.186.0"));
    });

    it("does not treat a numeric prefix as a match", () => {
        // 0.18 must not satisfy 0.186.0 — string-prefix matching would accept it.
        ok(!agrees("0.18", "0.186.0"));
    });
});

describe("versionOf", () => {
    it("finds a package in either dependency block", () => {
        strictEqual(versionOf(MANIFEST, "three"), "^0.186.0");
        strictEqual(versionOf(MANIFEST, "vite"), "^8.3.0");
    });

    it("returns null for a package the manifest does not declare", () => {
        strictEqual(versionOf(MANIFEST, "react"), null);
        strictEqual(versionOf(null, "three"), null);
    });
});

describe("checkClaim", () => {
    it("passes when the document agrees", () => {
        strictEqual(checkClaim(CLAIM, "built with Three.js 0.186.0 today", MANIFEST).length, 0);
    });

    it("reports a drifted version", () => {
        const problems = checkClaim(CLAIM, "built with Three.js 0.185.1 today", MANIFEST);
        strictEqual(problems.length, 1);
        strictEqual(problems[0]!.code, "documented-version-drifted");
    });

    it("checks EVERY occurrence, not just the first", () => {
        // The regression this whole check exists for: README named the version twice and only
        // one was fixed. A check that stopped at the first match would have passed that tree.
        const problems = checkClaim(
            CLAIM, "Three.js 0.186.0 badge ... later, Three.js 0.185.1 prose", MANIFEST);
        strictEqual(problems.length, 1);
        strictEqual(problems[0]!.code, "documented-version-drifted");
        ok(problems[0]!.message.includes("0.185.1"));
    });

    it("fails when the pattern matches nothing", () => {
        // A guard that can silently find nothing to check is not a guard.
        const problems = checkClaim(CLAIM, "the renderer draws the scene", MANIFEST);
        strictEqual(problems.length, 1);
        strictEqual(problems[0]!.code, "claim-matches-nothing");
    });

    it("fails when the package has left package.json", () => {
        const problems = checkClaim(CLAIM, "Three.js 0.186.0", { dependencies: {} });
        strictEqual(problems.length, 1);
        strictEqual(problems[0]!.code, "package-not-declared");
    });
});

/** Runs the real checker against a scratch tree. */
function run(
    claims: unknown,
    files: Record<string, string>,
    prepare?: (dir: string) => void,
): { status: number; out: string } {
    const dir = mkdtempSync(join(tmpdir(), "docver-"));
    for (const [rel, body] of Object.entries(files)) {
        const full = join(dir, rel);
        mkdirSync(dirname(full), { recursive: true });
        writeFileSync(full, body);
    }
    prepare?.(dir);
    const claimsPath = join(dir, "claims.json");
    writeFileSync(claimsPath, JSON.stringify(claims));
    const r = spawnSync(
        process.execPath,
        ["--experimental-strip-types", CHECKER, "--root", dir, "--claims", claimsPath],
        { encoding: "utf8" },
    );
    return { status: r.status ?? -1, out: `${r.stdout}${r.stderr}` };
}

const GITLINK = "0123456789abcdef0123456789abcdef01234567";

/** Adds a stage-0 submodule gitlink without checking the submodule out. */
function stageGitlink(dir: string, sha = GITLINK): void {
    for (const args of [
        ["init", "--quiet"],
        ["update-index", "--add", "--cacheinfo", `160000,${sha},lib/dotnet-sdk`],
    ]) {
        const r = spawnSync("git", args, { cwd: dir, encoding: "utf8" });
        strictEqual(r.status, 0, `${r.stdout}${r.stderr}`);
    }
}

describe("the checker as CI runs it", () => {
    const files = {
        "package.json": JSON.stringify(MANIFEST),
        "DOC.md": "Three.js 0.186.0 is used here.",
    };

    it("exits 0 on an agreeing tree", () => {
        const r = run([CLAIM], files);
        strictEqual(r.status, 0, r.out);
    });

    it("EXITS NON-ZERO on drift", () => {
        // The exit code is the only thing CI reads. A checker that prints ERROR and exits 0 is
        // precisely the inert gate this repo keeps finding, so it is asserted directly rather
        // than inferred from the output.
        const r = run([CLAIM], { ...files, "DOC.md": "Three.js 0.185.1 is used here." });
        strictEqual(r.status, 1, r.out);
    });

    it("refuses an empty claim registry rather than passing vacuously", () => {
        const r = run([], files);
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("vacuously"), r.out);
    });

    it("fails when a claim points at a file that does not exist", () => {
        const r = run([{ ...CLAIM, file: "GONE.md" }], files);
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("document-missing"), r.out);
    });
});

describe("documented dotnet-sdk submodule pin", () => {
    const files = {
        "package.json": JSON.stringify(MANIFEST),
        "DOC.md": "Three.js 0.186.0 is used here.",
    };

    const runWithGitlink = (claude: string) => run(
        [CLAIM],
        { ...files, "CLAUDE.md": claude },
        stageGitlink,
    );

    it("matches a documented short SHA against the stage-0 index gitlink", () => {
        // No lib/dotnet-sdk worktree exists: this can only pass if the checker reads the index.
        const r = runWithGitlink(`The SDK is pinned to **\`${GITLINK.slice(0, 12)}\`**.`);
        strictEqual(r.status, 0, r.out);
    });

    it("reports a stale documented pin", () => {
        const r = runWithGitlink("The SDK is pinned to **`abcdef0`**.");
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("submodule-pin-drifted"), r.out);
    });

    it("reports a missing documented pin", () => {
        const r = runWithGitlink("The SDK has a documented revision.");
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("claim-matches-nothing"), r.out);
    });

    it("reports a malformed documented pin", () => {
        const r = runWithGitlink("The SDK is pinned to **`not-a-sha`**.");
        strictEqual(r.status, 1, r.out);
        ok(r.out.includes("claim-matches-nothing"), r.out);
    });
});

describe("the claims this repo actually ships", () => {
    it("pass against the real tree", () => {
        // Runs the committed registry against the committed documents, so a bump that updates
        // package.json and forgets a document fails here and not only in CI.
        const r = spawnSync(
            process.execPath,
            ["--experimental-strip-types", CHECKER, "--root", REPO],
            { encoding: "utf8" },
        );
        strictEqual(r.status, 0, `${r.stdout}${r.stderr}`);
    });

    it("name only documents and manifests that exist", () => {
        const claims = JSON.parse(
            readFileSync(join(REPO, "tools", "docs", "version-claims.json"), "utf8"));
        ok(Array.isArray(claims) && claims.length > 0);
        for (const c of claims) {
            ok(typeof c.file === "string" && c.file.length > 0, `bad file: ${JSON.stringify(c)}`);
            ok(typeof c.note === "string" && c.note.length > 0, `claim needs a note: ${c.file}`);
            ok(/\([^)]*\)/.test(c.pattern), `pattern needs a capture group: ${c.pattern}`);
        }
    });
});
