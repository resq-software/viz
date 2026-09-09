---
# Trigger - when should this workflow run?
# On demand only, matching the other three agentic workflows in this repo — none of
# them runs per-PR either.
#
# It used to run on `pull_request: [opened]` and failed 12 of its last 25 runs, all at
# `Execute Gemini CLI` on free-tier Google AI Studio quota. So roughly half of all PRs
# opened here carried a red check that meant nothing, on a job that is not a required
# status check. A signal that is wrong half the time is worse than no signal: it teaches
# people to skip the checks list, which is where the real failures also appear.
#
# The audit itself still has value when it runs, and CodeRabbit already reviews every PR
# automatically — so this is available by dispatch when someone wants a second opinion,
# without spending a coin-flip red square on every PR to get it.
on:
  workflow_dispatch:

# Permissions - what can this workflow access?
permissions:
  contents: read
  issues: read
  pull-requests: read

# AI engine - Gemini (free Google AI Studio tier; avoids Copilot utility-model rate limits)
engine: gemini

# Network access
network: defaults

# Outputs - what APIs and tools can the AI use?
# `report-failure-as-issue: false` only suppresses the *failure* report. The
# compiler still wires up issue creation for noop, missing-tool and incomplete
# runs, so this workflow would file issues despite `issues: read`. Disable each
# one explicitly to keep the auditor comment-only.
safe-outputs:
  report-failure-as-issue: false
  add-comment:
    max: 10
  missing-tool: false
  noop: false

---

# ai-auditor

Audit the changes in this pull request for security vulnerabilities, logic bugs, or performance issues.

## Instructions

1.  Review all file changes in the current pull request.
2.  Identify potential security vulnerabilities (e.g., SQL injection, hardcoded secrets, insecure defaults).
3.  Look for logic bugs, edge cases, or potential runtime errors.
4.  Check for performance bottlenecks or inefficient code patterns.
5.  For each identified issue, provide a concise and constructive comment explaining the problem and suggesting a fix.
6.  Use the `add-comment` tool to post your feedback directly on the PR.

Be thorough but focus on high-impact issues. If no issues are found, post a brief summary comment stating that the audit passed.

## Setup

This workflow uses the Gemini engine and requires the `GEMINI_API_KEY` repository secret (free key from https://aistudio.google.com).
