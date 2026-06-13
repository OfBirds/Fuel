# Fuel — Full-Stack Template

A batteries-included starting point for a self-hosted full-stack app:
**ASP.NET Core (net10) + React/Vite/TypeScript + PostgreSQL**, shipped as a single
Docker image, with CI/CD, observability, email, and deploy tooling already wired up.

The point of this template is to skip re-solving the infrastructure every time.
Clone it, rename it, and start building your actual feature — staging and prod
deploys, migrations, logging, and release emails already work.

> This repo is a **GitHub template**. Click **“Use this template”** (or clone it),
> then run the rename script (see [Getting started](#getting-started)).

## What's included (the rails)

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
- **Auth** — register / login / reset-password with PBKDF2 password hashing.
  ⚠️ **The token is a demo placeholder — see [Before you ship](#before-you-ship).**
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
- **Claude Code tooling** — a `project-startup` skill and settings under `.claude/`.

## Tech stack

- **Backend**: ASP.NET Core, Entity Framework Core, Serilog, OpenTelemetry, MailKit
- **Frontend**: React, Vite, TypeScript
- **Database**: PostgreSQL
- **Containerization**: Docker & Docker Compose
- **CI/CD**: GitHub Actions + GHCR + a self-hosted runner

## Getting started

### 1. Create your project from the template

Use the GitHub “Use this template” button, or clone this repo. Then rename the
placeholder tokens (`Fuel` / `fuel`, the GHCR owner, ports, DB name):

```bash
# Linux / macOS / WSL / Git Bash
./scripts/rename.sh MyApp my-github-org

# Windows PowerShell
./scripts/rename.ps1 MyApp my-github-org
```

This rewrites namespaces enrichment, the solution file name, the GHCR image path,
compose project/DB names, ports, the PWA manifest/cache, and the docs. See
`scripts/rename.sh --help` for options (`--db`, `--ports`).

### 2. Run it locally

```bash
# 1. Postgres (+ pgAdmin) for local dev
docker compose up -d

# 2. Backend → http://localhost:5200
cd backend/Api && dotnet run

# 3. Frontend → http://localhost:3000 (proxies /api to :5200)
cd frontend && npm install && npm run dev
```

(There's also a Claude Code `project-startup` skill that automates this.)

### 3. Run the tests

```bash
dotnet test backend/Fuel.slnx -c Release      # backend
npm test --prefix frontend -- --run              # frontend
```

### 4. Deploy

Follow [`docs/deploy-runbook.md`](docs/deploy-runbook.md) to stand up the
self-hosted runner and env files. After that, push to `main` (staging) or
`release` (prod) and CI does the rest. See
[`docs/infrastructure.md`](docs/infrastructure.md) for the design.

## Before you ship

This template gets you running fast, but a few things are intentionally left as
**your** first tasks before any real or public deployment:

1. **🔒 Harden authentication.** The login token is a demo placeholder —
   `Base64(userId:ticks)`, unsigned and unverified, and there is **no
   `[Authorize]` / validation middleware**, so the API is effectively open.
   Replace it with real auth (e.g. `AddAuthentication().AddJwtBearer()` + a signed
   JWT and `[Authorize]` on protected controllers). See the `TODO(SECURITY)` in
   `backend/Api/Controllers/AuthController.cs`. Password hashing (PBKDF2) is fine.
2. **TLS / reverse proxy.** The default access model is plain HTTP on the LAN. Put
   a reverse proxy with TLS in front before exposing anything publicly.
3. **Secrets.** Fill real values into `/opt/fuel/.env.staging` and
   `.env.prod` on the host (never commit them). Add a `LICENSE` for your project.
4. **Review the env-gated features** (Seq, email/notifications, backups) and turn
   on what you want per environment.

## Project layout

```
backend/Api/          ASP.NET Core API (+ SPA served from wwwroot in the image)
backend/Api.Tests/    xUnit tests
frontend/             React + Vite SPA
deploy/               parameterized staging/prod compose + env templates
docs/                 infrastructure, deploy runbook, notifications, testing
scripts/              rename.sh / rename.ps1
.github/workflows/    CI/CD pipeline
.claude/              Claude Code skill + settings
```

## Docs

- [`docs/infrastructure.md`](docs/infrastructure.md) — CI/CD + hosting design
- [`docs/deploy-runbook.md`](docs/deploy-runbook.md) — one-time host/runner setup
- [`docs/notifications.md`](docs/notifications.md) — versioning + release emails
- [`docs/testing.md`](docs/testing.md) — test suites and how to run them
