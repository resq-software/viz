#!/usr/bin/env bash
#
# Copyright 2026 ResQ Systems, Inc.
# SPDX-License-Identifier: Apache-2.0
#
# Runs the Playwright browser suite against two real Kestrel servers.
#
# The console's room cookie is issued with `Secure = true` (SessionController),
# so a plain-HTTP origin gets no room at all and every room-scoped route answers
# 401. The browser suite therefore needs HTTPS, which needs a certificate, which
# is the only reason this wrapper exists: `playwright.config.ts` reads the PFX
# path and password out of the environment, and something has to put them there.
#
# Two ways in:
#
#   * RESQ_BROWSER_PFX points at an existing file and RESQ_BROWSER_PFX_PASSWORD
#     is set — CI hands us a certificate, we use it and touch nothing.
#   * Otherwise — a developer machine — export the ASP.NET Core development
#     certificate into a fresh mktemp directory under a throwaway password.
#
# The EXIT trap deletes the directory this script created and nothing else: on
# the caller-supplied path `$scratch` stays empty, so the trap has nothing to
# act on and the caller's certificate is never touched.
#
# Every export below happens in this process, which is the npm-script child.
# The invoking shell's environment is not modified.
set -Eeuo pipefail

scratch=''
cleanup() {
  if [ -n "$scratch" ]; then
    rm -rf -- "$scratch"
  fi
  return 0
}
trap cleanup EXIT

cd "$(dirname "$0")/.."

if [ -f "${RESQ_BROWSER_PFX:-}" ] && [ -n "${RESQ_BROWSER_PFX_PASSWORD:-}" ]; then
  echo "run-browser-tests: reusing certificate at ${RESQ_BROWSER_PFX}"
else
  scratch="$(mktemp -d)"
  RESQ_BROWSER_PFX="${scratch}/browser-verification.pfx"
  # Read once from the kernel CSPRNG. This password guards a certificate that
  # lives for the length of this process and is deleted by the trap.
  RESQ_BROWSER_PFX_PASSWORD="$(head -c 24 /dev/urandom | od -An -tx1 | tr -d ' \n')"
  dotnet dev-certs https --export-path "$RESQ_BROWSER_PFX" \
    --password "$RESQ_BROWSER_PFX_PASSWORD" --format Pfx >/dev/null
  echo "run-browser-tests: exported development certificate to ${RESQ_BROWSER_PFX}"
fi

export RESQ_BROWSER_PFX RESQ_BROWSER_PFX_PASSWORD

npx playwright test "$@"
