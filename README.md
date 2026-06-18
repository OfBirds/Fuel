# Indigo Swallow — self-hosted AI calorie tracker

> **Repo/codebase note:** the product is branded **Indigo Swallow** (part of a bird-named
> homelab ecosystem alongside the **CrimsonRaven** identity provider). The repository, code
> namespace, Docker/CI artifacts and infra all stay `fuel` — only user-facing strings + the
> app icon carry the brand.

Indigo Swallow is a minimalist, self-hosted **calorie tracker** with AI-assisted logging:
log a meal by typing it or snapping a photo, an AI provider estimates the calories
(and macros), and it files them under the day's meals against your goal. It ships
as a single Docker image — **ASP.NET Core (net10) + React/Vite/TypeScript +
PostgreSQL** — and installs as a **PWA**.

Food and weight data is about as personal as it gets, so it's built to be
**self-hosted**: it runs on infrastructure you control, not someone else's cloud.

> **Status: in use.** The infrastructure rails are wired and the product features
> (food catalogue, logging, day view, profile/weight, AI estimation, barcode lookup) are
> built and running on staging. Auth is delegated to **CrimsonRaven** SSO with a local
> email/password fallback. See [`docs/`](docs/) for the per-feature specs.

## What Indigo Swallow does (the product)

- **Log food three ways** — manually, by **typing** a description ("gurmanska
  pljeskavica 300 gr in a bun"), or from a **photo**. The AI paths pre-fill the
  same editable screen the manual path uses, so you always confirm/edit the
  result (including the intake time).
- **Food catalogue** — every food (a meal *or* an ingredient) is defined once,
  with a default unit and per-unit calories/macros; log entries reference it. A
  food can be composed of other foods as ingredients.
- **Meal-sectioned day view** — breakfast, lunch, dinner, and snacks, with a
  running total against your daily calorie goal.
- **Macros** — estimated and stored, hidden behind a Settings toggle so the
  default view stays simple.
- **Profile & weight** — height, sex, body frame, and year of birth drive BMI and
  a metabolism readout; a weight register tracks weigh-ins over time.
- **AI provider is chosen by the operator at deploy time** and is swappable
  (DeepSeek first) — never a user-facing setting.

## Under the hood (already wired)

These rails come from the project's self-hosted full-stack base and are in place:

- **Single-image deploy** — a multi-stage `Dockerfile` builds the Vite SPA, drops
  it into the backend's `wwwroot/`, and ASP.NET serves both the API and the SPA on
  one origin. No nginx, no CORS, no proxy.
- **CI/CD** (`.github/workflows/deploy.yml`) — on push: run tests → build the image
  → push to GHCR → deploy on a self-hosted runner. EF Core migrations are generated
  and applied as an explicit, gated step before the app is swapped.
- **Two environments** — `main` → staging, `release` → prod, each a fully isolated
  Docker stack (own DB, volumes, network, ports) selected by an env file.
- **Versioning** — `VERSION` (`MAJOR.MINOR`) + `github.run_number` → `MAJOR.MINOR.N`,
  baked into the image and exposed at `GET /api/version`.
- **Observability** — Serilog structured logs (rolling JSON file + console),
  shipped to [Seq](https://datalust.co/seq) with OpenTelemetry traces, every event
  tagged with the app version.
- **Auth** — **CrimsonRaven SSO first** (OIDC/PKCE), with a self-issued
  email/password JWT (PBKDF2) as the backup path; dual-auth, identity mapped onto existing
  users by verified email so no data is lost. Every API endpoint requires auth by default and
  is scoped to the token's own user. Set `JWT_SIGNING_KEY` before deploying and configure
  `OIDC_*` per stack — see [`docs/auth-crimsonraven.md`](docs/auth-crimsonraven.md) and
  [Before you ship](#before-you-ship).
- **Email** — MailKit `SmtpEmailSender` (port-based TLS), configured from `SMTP_*`
  env keys.
- **Release notifications** — on deploy, email opted-in users that a new version is
  out (idempotent, env-gated, with one-click unsubscribe).
- **Backups** — an env-gated background job that writes periodic JSON snapshots to
  disk with retention.
- **PWA** — installable, with a service worker and manifest.
- **Settings + user prefs** — a settings page and `GET/PUT /api/user/{id}/prefs`.
- **Tests** — xUnit + EF Core InMemory (backend) and Vitest + Testing Library
  (frontend), run in CI before anything is built or deployed.

## Tech stack

- **Backend**: ASP.NET Core, Entity Framework Core, Serilog, OpenTelemetry, MailKit
- **Frontend**: React, Vite, TypeScript
- **Database**: PostgreSQL
- **Containerization**: Docker & Docker Compose
- **CI/CD**: GitHub Actions + GHCR + a self-hosted runner

## Run it locally

```bash
# 1. Postgres (+ pgAdmin) for local dev
docker compose up -d

# 2. Backend → http://localhost:5200
cd backend/Api && dotnet run

# 3. Frontend → http://localhost:3000 (proxies /api to :5200)
cd frontend && npm install && npm run dev
```

(There's also a Claude Code `project-startup` skill that automates this.)

## Run the tests

```bash
dotnet test backend/Fuel.slnx -c Release      # backend
npm test --prefix frontend -- --run              # frontend
```

## Deploy

Follow [`docs/deploy-runbook.md`](docs/deploy-runbook.md) to stand up the
self-hosted runner and env files. After that, push to `main` (staging) or
`release` (prod) and CI does the rest. See
[`docs/infrastructure.md`](docs/infrastructure.md) for the design.

## Before you ship

A few things are deliberately left as first tasks before any real or public
deployment:

1. **🔒 Set `JWT_SIGNING_KEY`.** Auth now uses signed JWTs (HMAC-SHA256) with a
   default-deny authorization policy and per-user resource scoping. The signing key
   comes from `JWT_SIGNING_KEY`; if it's unset the app mints an *ephemeral* key and
   logs every user out on each restart. Set a real random secret (≥32 chars, e.g.
   `openssl rand -base64 48`) in each deploy env file. Consider whether you also need
   token revocation/refresh and roles — the current model is one account = one user.
2. **TLS / reverse proxy.** The default access model is plain HTTP on the LAN. Put
   a reverse proxy with TLS in front before exposing anything publicly.
3. **Secrets.** Fill real values into `/opt/fuel/.env.staging` and `.env.prod` on
   the host (`DB_PASSWORD`, `JWT_SIGNING_KEY`, SMTP, `PUBLIC_BASE_URL`; never commit
   them). Add a `LICENSE`.
4. **Review the env-gated features** (Seq, email/notifications, backups) and turn
   on what you want per environment.

## Project layout

```
backend/Api/          ASP.NET Core API (+ SPA served from wwwroot in the image)
backend/Api.Tests/    xUnit tests
frontend/             React + Vite SPA
deploy/               parameterized staging/prod compose + env templates
docs/                 infrastructure, deploy runbook, notifications, testing + feature specs
scripts/              rename.sh / rename.ps1 (template provenance; vestigial now)
.github/workflows/    CI/CD pipeline
.claude/              Claude Code skill + settings
```

## Docs

**Product / feature specs (design)**
- [`docs/food-catalogue-and-logging.md`](docs/food-catalogue-and-logging.md) — Phase 0: catalogue + manual logging + day view
- [`docs/profile-and-weight.md`](docs/profile-and-weight.md) — Phase 1: profile, weight register, metabolism/BMI, meal-pause
- [`docs/ai-estimation.md`](docs/ai-estimation.md) — Phase 2/3: AI calorie estimation from text & photo (multi-item, unit conversion, refine loop, camera)
- [`docs/ai-providers.md`](docs/ai-providers.md) — the deploy-time, swappable AI provider abstraction
- [`docs/barcode-lookup.md`](docs/barcode-lookup.md) — Phase 3: grocery barcode/EAN → official food definition (Open Food Facts)
- [`docs/food-sorting-and-ranking.md`](docs/food-sorting-and-ranking.md) — catalogue sort/ranking modes

**Platform**
- [`docs/auth-crimsonraven.md`](docs/auth-crimsonraven.md) — CrimsonRaven SSO (dual-auth, link-by-email, CrimsonRaven-first login)
- [`docs/infrastructure.md`](docs/infrastructure.md) — CI/CD + hosting design
- [`docs/deploy-runbook.md`](docs/deploy-runbook.md) — one-time host/runner setup
- [`docs/notifications.md`](docs/notifications.md) — versioning + release emails
- [`docs/testing.md`](docs/testing.md) — test suites and how to run them
