# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Fuel is a self-hosted full-stack app: **ASP.NET Core (net10) API + React/Vite/TypeScript SPA + PostgreSQL**, shipped as a single Docker image. It began life as a batteries-included template (CI/CD, observability, email, auth scaffold all pre-wired) — the README still documents the template rails. The actual product feature ("smart diet tracker") is largely unbuilt; `HomePage.tsx` is a near-empty shell. When adding the feature, you're building on the scaffold, not replacing it.

## Commands

Local dev needs three pieces up — Postgres, backend, frontend (in that order; the backend dies without Postgres). The `project-startup` skill automates this; manually:

```bash
docker compose up -d                        # Postgres (fuel-postgres, db/user/pass = fuel/postgres/postgres, :5432)
cd backend/Api && dotnet run                # backend → http://localhost:5200  (root returns 404; hit /api/version)
cd frontend && npm install && npm run dev   # frontend → http://localhost:3000 (proxies /api → :5200)
```

Tests:
```bash
dotnet test backend/Fuel.slnx -c Release            # backend (xUnit + EF Core InMemory)
npm test --prefix frontend -- --run                 # frontend (Vitest + Testing Library)
dotnet test backend/Fuel.slnx --filter "FullyQualifiedName~UserControllerTests"   # single backend test class
npm test --prefix frontend -- --run storage         # single frontend test file
```

EF Core migrations:
```bash
dotnet ef migrations add <Name> --project backend/Api    # create
dotnet ef database update --project backend/Api          # apply locally
```
Migrations also auto-apply on app startup (`Database.Migrate()` in `Program.cs`) as an idempotent safety net, and CI generates+applies an idempotent script as an explicit gated deploy step.

Build/run gotcha: `dotnet build`/`run` fails if the app is already running and holding the assembly — stop the running instance first.

## Architecture

**Single origin, single image.** There is no separate web server, no CORS, no proxy in production. The Dockerfile builds the Vite SPA, copies `frontend/dist` into the backend's `wwwroot/`, and ASP.NET serves both: `MapControllers()` matches `/api/*` first, `MapFallbackToFile("index.html")` handles client-side routes. In dev only, Vite's proxy bridges :3000 → :5200. Keep all API routes under `/api` or the SPA fallback will swallow them.

**Configuration is flat env vars, not nested config sections.** `Program.cs` reads `DB_HOST`/`DB_PORT`/`DB_NAME`/`DB_USER`/`DB_PASSWORD` and `SMTP_*` directly from `builder.Configuration[...]` with hardcoded localhost/postgres fallbacks. Follow this `KEY` style for new config rather than `appsettings.json` sections. **One deliberate exception:** the AI provider registry (`docs/ai-providers.md`) is a structured, hot-reloadable JSON file (`AI_CONFIG_FILE`, bound via `IOptionsMonitor` with `reloadOnChange`) because it's an N-entry list that must change on a running container without redeploy. Secrets still follow the flat rule — provider key VALUES live in `AI_KEY_<NAME>` env vars, referenced from the JSON by name.

**Backend layering.** Controllers (`backend/Api/Controllers`) → services (`backend/Api/Services`) → `AppDbContext` (EF Core / Npgsql). Auth logic lives in `AuthService` (PBKDF2 hashing); controllers stay thin. DTOs in `backend/Api/DTOs`, EF entities in `backend/Api/Models`. To add a persisted entity: add a model, a `DbSet` + `OnModelCreating` config in `AppDbContext`, then a migration.

**Env-gated hosted services.** `ReleaseNotifier` (emails opted-in users on a new version) and `BackupService` (periodic JSON snapshots) are registered unconditionally but no-op unless their env keys are set. Serilog ships to Seq only when `SEQ_URL` is set; OpenTelemetry traces likewise. Locally these stay dark — that's expected.

**Frontend state.** Two React contexts wrap the app: `AuthProvider` (login/register/logout, persists `user`+`token` to `localStorage`, talks to `/api/auth/*` via `fetch`) and `ThemeProvider`. There's no router-level auth guard — `AppContent` just renders `LoginPage` when `user` is null. All `localStorage` access goes through `src/lib/storage.ts` (prefixed, typed getters/setters) so it can be swapped for IndexedDB later — add new persisted prefs there, don't call `localStorage` directly.

**Versioning.** `VERSION` (MAJOR.MINOR, hand-bumped) + `github.run_number` → full version, baked into the image (`APP_VERSION`), exposed at `GET /api/version`, read into the SPA bundle via `__APP_VERSION__` (vite `define`), and tagged onto every log event.

## ⚠️ Auth is a demo placeholder

The login token is `Base64(userId:ticks)` — unsigned, unverified, and there is **no `[Authorize]` or validation middleware anywhere**, so every API endpoint is effectively open (e.g. `UserController` trusts the `{userId}` in the route). See `TODO(SECURITY)` in `AuthController.cs`. Don't assume requests are authenticated. PBKDF2 password hashing in `AuthService` is real and fine. Replace with signed-JWT auth before any real deployment.

## Deploy

Push-to-deploy via `.github/workflows/deploy.yml`: `main` → staging stack, `release` → prod stack. Each branch runs tests → builds/pushes the image to GHCR → applies the gated migration script → deploys on a self-hosted runner. See `docs/deploy-runbook.md` (one-time host setup) and `docs/infrastructure.md` (design).
