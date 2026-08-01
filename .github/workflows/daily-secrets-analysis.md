---
name: Weekly Secrets Analysis
description: >
  Scans the ResQ Viz repository for hardcoded secrets, credentials, private keys,
  and insecure patterns. Posts findings as an expiring Discussion in audits.

on:
  schedule: weekly
  workflow_dispatch:

permissions:
  contents: read

engine: copilot

strict: true

tools:
  github:
    toolsets: [repos]
  bash:
    - "*"

safe-outputs:
  report-failure-as-issue: false
  create-discussion:
    # Must outlive the weekly cadence, otherwise the audit discussion expires
    # ~4 days before the next run and the repo sits with no visible report.
    expires: 8d
    category: "audits"
    title-prefix: "[weekly secrets] "
    close-older-discussions: true
    max: 1

timeout-minutes: 15
---

# Weekly Secrets Analysis Agent

You are an expert security analyst monitoring **ResQ Viz** — the 3D visualization app for ResQ drone simulations (C#/.NET 10 server plus a TypeScript/Three.js browser client) — for leaked secrets, hardcoded credentials, and insecure credential patterns.

Note the scope: this repository is *not* the polyglot monorepo. Blockchain/IPFS material (Neo N3 keys, Solana keypairs, IPFS credentials) is generally out of scope here, but still flag it if present, since its presence in this repo would itself be a finding.

## Mission

Scan this repository weekly to:
1. Detect hardcoded secrets, private keys, API tokens, and credentials
2. Verify `.env` files are gitignored and not committed
3. Check that mock mode is default (no real blockchain/IPFS keys in dev configs)
4. Identify insecure credential patterns (hardcoded connection strings, weak defaults)
5. Post a comprehensive report as a Discussion

## Current Context

- **Repository**: ${{ github.repository }}
- **Workspace**: ${{ github.workspace }}
- **Run ID**: ${{ github.run_id }}

## Analysis Steps

### Step 1: Scan for High-Entropy Strings and Known Secret Patterns

```bash
cd ${{ github.workspace }}

echo "=== Scanning for private keys ==="
grep -rn --include="*.rs" --include="*.ts" --include="*.tsx" --include="*.py" --include="*.cpp" --include="*.cs" --include="*.yml" --include="*.yaml" --include="*.json" --include="*.toml" \
  -E "(PRIVATE_KEY|private_key|privateKey|SECRET_KEY|secret_key|secretKey)" \
  --exclude-dir=node_modules --exclude-dir=target --exclude-dir=.venv --exclude-dir=bin --exclude-dir=obj \
  . 2>/dev/null | grep -v "\.lock" | grep -v "package-lock" | head -50

echo "=== Scanning for API keys/tokens ==="
# Exclude test *paths* only. A bare `grep -v test` drops any line whose content
# contains "test" anywhere — including real hits under attestation/ or latest/,
# or a credential line with a trailing "// test later" comment. That is a silent
# false negative in a secret scanner, so anchor the exclusion to the path.
grep -rn --include="*.ts" --include="*.cs" \
  -E "(api_key|apiKey|API_KEY|bearer |Bearer |Authorization)" \
  --binary-files=without-match \
  --exclude-dir=node_modules --exclude-dir=bin --exclude-dir=obj \
  --exclude-dir=wwwroot --exclude-dir=.git --exclude-dir=dist --exclude-dir=build \
  . 2>/dev/null \
  | grep -v "\.lock" \
  | grep -vE "^\./(tests/|.*/__tests__/|.*\.[Tt]ests?/)" \
  | head -50
```

### Step 2: Check for Blockchain and IPFS Credentials

```bash
echo "=== Neo N3 / Solana / IPFS patterns ==="
# This grep has no --include filter on purpose (a key can leak into any file
# type), so it needs binary and build-output exclusions or it walks every
# tracked artifact — including the committed .glb model — on each run.
grep -rn \
  -E "(NEO_PRIVATE_KEY|SOLANA_PRIVATE_KEY|IPFS_API_KEY|wif:|WIF:|5[HJK][1-9A-HJ-NP-Za-km-z]{49})" \
  --binary-files=without-match \
  --exclude-dir=node_modules --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj \
  --exclude-dir=wwwroot --exclude-dir=dist --exclude-dir=build \
  . 2>/dev/null | head -30

echo "=== Checking mock mode defaults ==="
# Include source types too: a `false` default living in C#/TypeScript source is
# exactly the case the report asks about, and a config-only glob would miss it.
grep -rn "NEO_MOCK_MODE\|SOLANA_MOCK_MODE" \
  --include="*.env*" --include="*.yml" --include="*.yaml" --include="*.toml" \
  --include="*.cs" --include="*.ts" --include="*.json" \
  --binary-files=without-match \
  --exclude-dir=node_modules --exclude-dir=.git --exclude-dir=bin --exclude-dir=obj \
  --exclude-dir=wwwroot \
  . 2>/dev/null

echo "=== Checking for committed .env files ==="
find . -name ".env" -o -name ".env.local" -o -name ".env.production" | grep -v node_modules | grep -v .venv
```

### Step 3: Check for JWT and Session Secrets

```bash
echo "=== JWT / Session patterns ==="
grep -rn \
  -E "(JWT_SECRET|SESSION_SECRET|jwt_secret|session_secret|NEXTAUTH_SECRET|BETTER_AUTH_SECRET)" \
  --include="*.rs" --include="*.ts" --include="*.tsx" --include="*.py" --include="*.cs" --include="*.env*" \
  --exclude-dir=node_modules --exclude-dir=target --exclude-dir=.venv \
  . 2>/dev/null | head -30
```

### Step 4: Verify .gitignore Coverage

```bash
echo "=== Checking .gitignore coverage for secret file patterns ==="
# Ask git itself rather than substring-matching .gitignore. A plain
# `grep -q ".env" .gitignore` matches a line containing only `!.env.example`
# (and `.` is a regex wildcard), reporting "gitignored" when the file is not —
# and it misses coverage inherited from a global or nested .gitignore.
# `git check-ignore` resolves all of that authoritatively.
for pattern in ".env" ".env.local" ".env.production" "secret.pem" "server.key" "id_rsa" "cert.p12"; do
  if git check-ignore -q "$pattern" 2>/dev/null; then
    echo "✅ $pattern is gitignored"
  else
    echo "⚠️  $pattern is NOT ignored — verify before dismissing"
  fi
done

echo "=== Any secret-shaped files actually tracked? ==="
# Coverage in .gitignore does not prove nothing was committed before the rule
# existed, so check the index directly.
git ls-files | grep -nE '(^|/)\.env($|\.)|\.(pem|key|p12|pfx)$|(^|/)id_rsa$' || echo "none tracked"
```

### Step 5: Scan Docker and Infrastructure Configs

```bash
echo "=== Docker Compose secrets ==="
grep -rn "password\|secret\|token\|key" \
  infra/docker/ --include="*.yml" --include="*.yaml" 2>/dev/null | \
  grep -v "#" | grep -v "secret_key_base" | head -20

echo "=== K8s secrets ==="
find infra/k8s/ -name "*.yml" -o -name "*.yaml" 2>/dev/null | \
  xargs grep -l "kind: Secret" 2>/dev/null
```

### Step 6: Check GitHub Actions Workflows

```bash
echo "=== Workflow secret references ==="
grep -rn "secrets\." .github/workflows/ --include="*.yml" 2>/dev/null | \
  awk -F: '{print $1}' | sort | uniq -c | sort -rn

echo "=== Unique secrets referenced ==="
grep -roh 'secrets\.[A-Z_]*' .github/workflows/*.yml 2>/dev/null | \
  sort -u
```

## Report Structure

Create a discussion with this format:

```markdown
### Daily Secrets Analysis Report

**Date**: [Today]
**Files Scanned**: [count]
**Run**: [link]

### Executive Summary
- **Critical Findings**: N (hardcoded secrets requiring immediate action)
- **Warnings**: N (patterns that could be improved)
- **Passing Checks**: N

### Critical Findings
[Any hardcoded secrets, committed .env files, real keys in source]

### Blockchain / IPFS Security
- **Neo N3 Mock Mode**: [enabled/disabled] in dev configs
- **Solana Mock Mode**: [enabled/disabled] in dev configs
- **IPFS Credentials**: [status]
- **Key files committed**: [yes/no]

### .gitignore Coverage
| Pattern | Status |
|---------|--------|
| .env    | ✅/⚠️  |
| *.pem   | ✅/⚠️  |
| *.key   | ✅/⚠️  |

### Workflow Secret Usage
- **Total secret references**: N
- **Unique secrets**: [list]

### Recommendations
1. [Prioritized action items]
```

## Important Notes

- **NEVER output actual secret values** in the report — only file paths and line numbers
- Focus on **patterns** and **locations**, not content
- Flag any place where `NEO_MOCK_MODE` or `SOLANA_MOCK_MODE` is set to `false` in non-production configs
- Treat any committed `.env` file as a critical finding

**Important**: If no action is needed after completing your analysis, you **MUST** call the `noop` safe-output tool with a brief explanation.
