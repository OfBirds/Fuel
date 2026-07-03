# Infrastructure & Deployment

> Status: **as-built**. The Dockerfile, parameterized compose, CI workflow, and
> runner are all in place. This documents how Fuel actually deploys. (Open item:
> a `release` branch / prod stack hasn't been brought up yet — staging on `main`
> is the active path.)

## Goal

Two independent, automatically-deployed environments on the homelab:

| Environment | Source branch | Trigger           | Purpose        |
| ----------- | ------------- | ----------------- | -------------- |
| **staging** | `main`        | push to `main`    | test / preview |
| **prod**    | `release`     | push to `release` | the real thing |

"Independent" means each is a fully separate Docker stack — own database, volumes,
network, secrets, ports. Wiping staging can never touch prod.

## What we deploy

Three pieces (see [README](../README.md)) shipped as **one image**: a multi-stage
`Dockerfile` builds the Vite SPA, copies `dist/` into the backend's `wwwroot/`, and
ASP.NET serves the SPA (`UseStaticFiles()` + fallback to `index.html`) on the same
origin as the API. `/api` is same-origin — **no nginx, no proxy, no CORS**. One
image, one app container per stack.

**Versioning.** `MAJOR.MINOR.<run_number>` — `MAJOR.MINOR` from the repo `VERSION`
file, patch from `github.run_number`. CI passes it (+ git sha, build time) into the
image as build-args (`APP_VERSION`/`GIT_COMMIT`/`BUILD_TIME`), tags the image
`:<version>` alongside `:staging`/`:prod`/`:<sha>`, and the app exposes it at
`GET /api/version`. See [notifications.md](notifications.md) for how this drives
release emails.

## Where it runs

**Host: the Debian VM `source`.** Docker in a full VM is the supported, boring path
(vs. Docker-in-LXC, which needs nesting/`keyctl`/`fuse-overlayfs` and breaks on
Proxmox upgrades). Both stacks run on this same VM, isolated by Docker
project/network/volume namespacing.

Fuel shares the host with other template-based apps (e.g. ThoseDaysApp), so its
**host ports are deconflicted** — see below.

## How a push reaches the homelab

The homelab is **not** exposed to the internet. A **self-hosted GitHub Actions
runner** on the VM connects *outbound* to GitHub and waits for jobs — **zero
inbound ports**. On a push, GitHub Actions:

1. Runs the test suites (gate).
2. Builds the single image and pushes it to **GHCR** (`ghcr.io/ofbirds/fuel`),
   tagged per environment.
3. Generates an **idempotent EF migration script** and applies it (the loud gate —
   a bad migration fails the deploy, old container keeps running).
4. Hands off to the runner, which pulls and brings the stack up
   (`docker compose pull && docker compose up -d --wait`).

**Runners are per-repo.** Fuel has its **own** runner instance under host user `at`
at `/home/at/actions-runner-fuel` (systemd service named from the registration — still
`actions.runner.Trifunovich-Fuel.fuel` from before the repo moved to the OfBirds org;
the label is cosmetic and the runner followed the transfer), separate from other apps'
runners. Fuel's workflow targets `runs-on: [self-hosted]`.
See [deploy-runbook.md](deploy-runbook.md).

## Stack layout per environment

One `docker compose` project per environment; same `deploy/docker-compose.yml`
template, two env files (`/opt/fuel/.env.staging`, `/opt/fuel/.env.prod`, **not
committed**). Services:

- **`postgres`** — own volume per stack; host port published for DBeaver
  (`DB_HOST_PORT`).
- **`app`** — the GHCR image (API + baked SPA), reads `DB_*` and `AI_*`/`SMTP_*`
  from env. Published on the LAN at `APP_PORT`.
- **`seq`** — [Seq](https://datalust.co/seq) log server; app ships Serilog to it
  (`SEQ_URL=http://seq`); UI published at `SEQ_PORT`.

**Host ports (deconflicted from other apps on the VM):**

| | staging | prod |
|---|---|---|
| App (`APP_PORT` → 8080) | **9223** | **9224** |
| Postgres (`DB_HOST_PORT` → 5432) | **5435** | **5436** |
| Seq UI (`SEQ_PORT` → 80) | **9233** | **9234** |

Isolation: distinct compose project names (`fuel-staging`/`fuel-prod`), networks,
volumes, and the host ports above.

## Access model

No reverse proxy for v1 — each stack is reached **directly over the LAN** by the
VM's address + the stack's port. Plain HTTP, no public exposure/TLS yet. Because
it's plain HTTP, the container runs without HTTPS redirection; the SPA + API share
one origin so there's no proxy to configure (the Vite dev proxy stays dev-only).

## Decisions
- **Migrations → both.** CI applies an idempotent script (gate) **and**
  `Database.Migrate()` runs on startup (`Program.cs`) as an idempotent safety net.
- **Hosting → Debian VM `source`**, both stacks on it.
- **Delivery → self-hosted runner + GHCR**, no inbound ports.
- **Access → LAN only**, no reverse proxy/TLS for v1.
- **Secrets → `/opt/fuel/.env.*` on the VM** (runtime, never committed) + GitHub
  Actions secrets (build). Plain env files are fine for a homelab.

## Deferred
- **HTTPS/TLS + reverse proxy** — staying plain HTTP on the LAN for v1; the
  friction is certificates. Done later alongside the prod public address (DuckDNS +
  nginx; see [notifications.md](notifications.md) §5).
- **Prod stack** — `release` branch not yet created; staging on `main` is live
  first.
