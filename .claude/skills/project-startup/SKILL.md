---
name: project-startup
description: Start, restart, build, and drive the Fuel app locally (ASP.NET Core backend + React/Vite frontend + Postgres). Use when asked to run, start, restart, build, or screenshot the app, or verify a change in the real app.
---

# Fuel startup steps

Full-stack app: ASP.NET Core (net10.0) backend, React + Vite frontend,
PostgreSQL via Docker. Three pieces must be up: Postgres, backend, frontend.

Paths below are relative to the repo root. Adjust the shell commands for your OS
(examples are bash; PowerShell equivalents noted where they differ).

## 1. Postgres (first — backend dies without it)

```bash
docker compose up -d
docker exec -e PGPASSWORD=postgres fuel-postgres pg_isready -U postgres
```

Container `fuel-postgres`, db `fuel`, user/pass `postgres`/`postgres`,
port 5432. Connection details also live in `appsettings.json` (the backend reads
`DB_*` env vars, falling back to these).

## 2. Backend → http://localhost:5200

```bash
cd backend/Api
dotnet run
```

- HTTP profile is **:5200** (see `Properties/launchSettings.json`); the root path
  returns 404 — that's expected, hit a real route like `/api/version`.
- Serilog writes a daily compact-JSON log to `backend/Api/logs/log-<date>.json`.
- **Rebuild gotcha:** `dotnet build`/`run` fails if the app is already running and
  holding the built assembly. Stop the running instance first.
- **Schema changes:** apply migrations with
  `dotnet ef database update --project backend/Api`.

## 3. Frontend → http://localhost:3000

```bash
cd frontend
npm install   # first time
npm run dev
```

Vite is configured for **:3000** and proxies `/api` → `http://localhost:5200`
(see `vite.config.ts`). HMR picks up source edits; no restart needed.

### Driving it with the Claude Preview tool

`.claude/launch.json` runs the frontend dev server. If :3000 is held by a
manually-started Vite, free it first before launching.

React inputs are controlled — `preview_fill` sets the DOM value but doesn't fire
React's `onChange`. To drive a form via the preview, set values with a
native-setter + `input` event, or just `preview_click` real elements (clicks do
dispatch). Example login helper:

```js
(() => {
  const set = (el, val) => {
    const d = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value');
    d.set.call(el, val);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  };
  set(document.querySelector('input[type="email"]'), 'you@example.com');
  set(document.querySelector('input[type="password"]'), 'password123');
  [...document.querySelectorAll('button')].find(b => /sign in/i.test(b.textContent))?.click();
})()
```

Note: React state updates after the eval returns, so don't read the resulting DOM
in the same `preview_eval` — wait a tick or use a follow-up call.

## Smoke checks

```bash
# backend up (returns version JSON)
curl http://localhost:5200/api/version
# frontend up (200)
curl -I http://localhost:3000
```

## Dev helpers

- **Reset a password** (no email infra needed): `POST /api/auth/reset-password`
  with `{ "email": "...", "newPassword": "..." }` (min 8 chars).
- **Inspect data:**
  `docker exec -e PGPASSWORD=postgres fuel-postgres psql -U postgres -d fuel -c 'SELECT * FROM "Users";'`
