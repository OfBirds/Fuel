# Infrastructure & Deployment Plan

> Status: **planning / draft**. Nothing here is implemented yet. This document
> describes the intended CI/CD and hosting setup so we can agree on the shape
> before writing any Dockerfiles, workflows, or compose files.

## Goal

Two independent, automatically-deployed environments running on the homelab:

| Environment | Source branch | Trigger            | Purpose            |
| ----------- | ------------- | ------------------ | ------------------ |
| **staging** | `main`        | push to `main`     | test / preview     |
| **prod**    | `release`     | push to `release`  | the real thing     |

"Independent" means each environment is a fully separate Docker stack: its own
database, its own volumes, its own network, its own secrets. Nothing is shared.
Wiping or breaking staging can never touch prod.

> Note: the repo's default branch is `main` (not `master`). A `release` branch
> does not exist yet and will be created when we wire up prod.

## What we are deploying

The app is three pieces (see [README](../README.md)):

- **backend** — ASP.NET Core Web API, talks to Postgres via EF Core.
- **frontend** — React + Vite, built to static files.
- **database** — PostgreSQL.

The frontend and backend ship as **one image**: a multi-stage Docker build
compiles the Vite app and copies `dist/` into the backend's `wwwroot/`, and
ASP.NET serves the SPA (`UseStaticFiles()` + fallback to `index.html`) on the
same origin as the API. So `/api` is just same-origin — **no nginx, no proxy
config, no CORS**. One image, one app container per stack.

**Versioning.** The version is `MAJOR.MINOR.<run_number>` — `MAJOR.MINOR` from the
`VERSION` file at the repo root, the patch from `github.run_number`. CI passes it
(plus git sha and build time) into the image via Docker build-args
(`APP_VERSION`/`GIT_COMMIT`/`BUILD_TIME`), tags the image `:<version>` alongside
`:staging`/`:prod`/`:<sha>`, and the app exposes it at `GET /api/version`. See
[notifications.md](notifications.md) for how this drives release emails.

Today `docker-compose.yml` only runs Postgres + pgAdmin for local dev. There is
**no Dockerfile yet** — that's the first thing to build.

## Where it runs

**Host: the Debian VM** (not the Ubuntu LXC).

Reasoning: Docker inside an unprivileged Proxmox LXC works but is fiddly
(requires nesting, `keyctl`, often `fuse-overlayfs`, and tends to break on
Proxmox/kernel upgrades). Docker in a full VM is the supported, boring path,
which is what we want for something meant to stay up unattended.

Both environments (staging + prod) run as separate stacks on this same VM.
That's fine — they're isolated by Docker project/network/volume namespacing.
If prod ever needs to move to its own VM later, nothing about the design
changes; we just point a second runner at it.

## How a push reaches the homelab

The homelab must **not** be exposed to the internet for this. Chosen approach:

### Self-hosted GitHub Actions runner on the Debian VM

- The runner connects *outbound* to GitHub and waits for jobs. **Zero inbound
  ports** opened on the homelab.
- On a push, GitHub Actions:
  1. Builds the single app image (frontend baked into the backend).
  2. Pushes it to **GHCR** (GitHub Container Registry), tagged per environment.
  3. Hands off to the self-hosted runner, which pulls the new image and
     restarts the relevant stack (`docker compose pull && docker compose up -d`).

Why this over the alternatives:

- **vs. SSH from GitHub-hosted runners** — would require exposing SSH (or a
  tunnel) to the homelab. More attack surface, more to maintain.
- **vs. Watchtower / image polling** — works, but gives less control over
  *when* and *in what order* things update, and no clean place to run DB
  migrations.

## Stack layout per environment

Each environment is one `docker compose` project. Same compose template, two
different env files:

```
deploy/
  docker-compose.yml        # parameterized template (image tags, ports via vars)
  .env.staging              # staging secrets/config (NOT committed)
  .env.prod                 # prod secrets/config    (NOT committed)
```

Services in each stack:

- `postgres` — own volume (e.g. `pgdata-staging` vs `pgdata-prod`), not exposed
  to the host except where needed.
- `app` — the single image from GHCR (API + baked-in SPA), reads DB connection
  from env (the backend already takes `DB_HOST` / `DB_PORT` / `DB_NAME` /
  `DB_USER` / `DB_PASSWORD`, see `backend/Api/Program.cs`). This is the one port
  exposed to the LAN per stack.
- `seq` — [Seq](https://datalust.co/seq) log server for structured-log
  monitoring. The app ships Serilog events to it (`SEQ_URL=http://seq`); the UI
  is published on the LAN per stack (`SEQ_PORT`, staging `9133` / prod `9134`).
  Auth is off (LAN-only); every event is tagged with `Application` + `Version`.

Isolation is enforced by:

- distinct compose project names (`fuel-staging`, `fuel-prod`),
- distinct networks,
- distinct named volumes,
- distinct host ports per environment (reached over the LAN by IP/hostname).

## Access model

No reverse proxy for v1. Each stack is reached directly over the **local network
only**, by the VM's IP / hostname plus the stack's port. No public exposure, no
TLS termination layer for now. (A reverse proxy may be added later, by hand,
outside this plan.)

Because everything is plain HTTP on the LAN:

- Backend's `app.UseHttpsRedirection()` (in `Program.cs`) should be **dropped /
  disabled inside the container** — otherwise it forces HTTPS that isn't there.
- The SPA and the API are served from the same origin by the one `app`
  container, so there's no proxy to configure (the Vite dev proxy in
  `vite.config.ts` stays dev-only).

## Decisions

- **Migrations → both.** Run EF Core migrations as an explicit CI deploy step
  *and* via `context.Database.Migrate()` on backend startup.
  - Note: this project does **not** auto-migrate today — `Program.cs` only calls
    `AddDbContext`. EF Core does not apply migrations unless `Migrate()` is
    called explicitly; the startup call must be added.
  - The CI step is the loud gate (a bad migration fails the deploy, old
    container keeps running). The startup call is an idempotent safety net — a
    no-op when CI already applied everything.
- **Hosting → Debian VM**, both stacks on it (see above).
- **Delivery → self-hosted runner + GHCR**, no inbound ports (see above).
- **Access → LAN only, direct by IP/hostname + port**, no reverse proxy / TLS
  for v1.
- **pgAdmin → local-dev only.** Not shipped to staging/prod. DBeaver over the
  LAN is used to explore the prod DB.
- **Secrets → GitHub Actions secrets (build) + `.env.staging` / `.env.prod`
  files on the VM (runtime).** Files are never committed. Plain env files are
  fine for a homelab; not using Docker secrets / Vault for v1.

## Deferred

- **HTTPS / TLS.** Staying plain HTTP on the LAN for v1. The friction isn't the
  proxy, it's certificates: self-signed → browser warnings + unhappy PWA service
  worker; own CA (`mkcert`) → must install the root cert on every device incl.
  phones; Let's Encrypt → needs a real domain + DNS-01. To be done later, the
  right way, alongside the eventual reverse proxy.
- **Backups.** Out of scope for v1, but prod Postgres will want a backup job
  eventually. Flagging so it's not forgotten.

## Rough implementation order (when we start)

1. Write the multi-stage `Dockerfile` (build frontend → copy into backend
   `wwwroot/` → publish backend); add `UseStaticFiles()` + SPA fallback and the
   `Migrate()` call to `Program.cs`, and drop `UseHttpsRedirection()`.
2. Write the parameterized `deploy/docker-compose.yml`.
3. Stand up the self-hosted runner on the Debian VM.
4. Write the GitHub Actions workflow (build → push GHCR → deploy via runner).
5. Bring up staging from `main`, verify end-to-end.
6. Create the `release` branch and bring up prod the same way.
