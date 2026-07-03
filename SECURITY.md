# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems.

Report privately via GitHub's **[Report a vulnerability](https://github.com/OfBirds/Fuel/security/advisories/new)**
(Security → Advisories) so we can triage and fix it before it's disclosed.

Include what you can: affected version (`GET /api/version`), a description, and
steps to reproduce. We'll acknowledge the report and keep you updated on the fix.

## Scope

Fuel is self-hosted software. Each deployment is operated by whoever runs it, so
issues affecting a *live instance* (leaked credentials, an exposed server) should go
to that instance's operator. Report **code** vulnerabilities in this repository here.

## Supported versions

Fixes land on the latest release. There are no long-term support branches — run a
current build.
