# Security Remediation TODO

Tracking fixes for vulnerabilities identified in the 2026-04-07 security assessment.

## Critical

- [x] 1. ~~API key authentication~~ Skipped (LAN simulation tool; hardening approach chosen instead)
- [x] 2. **HTTPS + HSTS** — Kestrel TLS on :5001, `UseHttpsRedirection()`, `UseHsts()` (AUTH-VULN-02)
- [x] 3. **Rate limiting** — fixed-window per-IP: 10/min destructive, 60/min general (AUTH-VULN-03)

## High

- [x] 4. **Drone count cap** — max 50, returns 429 on overflow (AUTHZ-VULN-06)
- [x] 5. **Fix reset-before-validate** — scenario name validated before `_sim.Reset()` (AUTHZ-VULN-12)
- [x] 6. **Cache-Control headers** — `no-store` on all `/api/` responses (AUTH-VULN-04)
- [x] 7. **Security headers** — X-Content-Type-Options, X-Frame-Options, CSP

## Medium

- [x] 8. **Float boundary validation** — Infinity rejected in position, target, windSpeed, windDirection
- [x] 9. **Validate fault droneId** — 404 if drone doesn't exist
- [x] 10. **Pin Vite.AspNetCore version** — `1.12.0` instead of `1.*`
