# Testing

> How to run the test suites and what each one covers.

## Harness layout

| Suite | Location | Framework | Type |
|-------|----------|-----------|------|
| Backend | `backend/Api.Tests/` | xUnit + EF Core InMemory | Unit + service-level integration |
| Frontend | `frontend/src/**/*.test.{ts,tsx}` | Vitest + Testing Library | Unit + component render |

Both stacks run in CI on every push to `main` and `release` before the image is built or
deployed (`.github/workflows/deploy.yml`, `test` job).

## Running tests

### Backend

```bash
# From the repo root:
dotnet test backend/Fuel.slnx -c Release
```

The solution file (`backend/Fuel.slnx`) references both `Api` and `Api.Tests`, so a
single `dotnet test` discovers everything. EF Core InMemory is used for database tests —
no real PostgreSQL needed.

### Frontend

```bash
# From the repo root:
npm ci --prefix frontend          # first time only
npm test --prefix frontend -- --run
```

Or from inside `frontend/`:

```bash
npm ci
npm test           # single run
npm run test:watch # watch mode
```

## What each suite covers

These are the starter tests — worked examples of each style (pure logic, EF
InMemory DB, controller, background service, frontend unit). Add feature tests
alongside them as the product is built out.

### Backend — `Api.Tests`

| File | What it tests |
|------|---------------|
| `BackupServiceTests.cs` | The periodic backup writer: `ParseInterval` mapping (daily/weekly/monthly/default), `RunBackupAsync` writing a snapshot with the convention name, no-op when there are no users, that secrets are never written, and retention/pruning to `BACKUP_KEEP`. Uses EF Core InMemory + a temp dir. |
| `ReleaseNotifierTests.cs` | `BuildEmail` (HTML bullet wrapping, encoding, empty notes, plain-text variant) and `ExecuteAsync` (idempotency via `last_notified_version`, disabled/dev/missing-version idle paths, best-effort loop where one failure doesn't block others, recipient filter for `IsActive && NotifyReleases`). Uses a fake `IEmailSender` and EF Core InMemory. |
| `UnsubscribeControllerTests.cs` | Token-scoped unsubscribe: a valid token clears the release opt-in, unknown token → 404, empty token → 400. |
| `UserControllerTests.cs` | The user-prefs endpoints: GET returns current values, unknown user → 404, PUT persists and echoes the new value. |

### Frontend — Vitest

| File | What it tests |
|------|---------------|
| `lib/storage.test.ts` | localStorage helpers: the `app:` key prefix, `getAutoUpdate`/`getFontScale` defaults, font-scale round-trip, malformed-JSON resilience, and theme save/read. |

## Planned — real-dependency integration tier (deferred)

> **Sequencing:** scaffold this *after* the AI-text and AI-photo features land, not
> before. Those features add the first external dependency (the DeepSeek nutrition
> estimator), and the same suite that gives us real-Postgres coverage is where the
> provider-call tests will live — so it's cheaper to stand it up once, then.

**Why it's needed (two reasons):**

1. **EF InMemory is not Postgres.** It silently ignores provider-specific rules, so a
   green unit suite can still ship a persistence bug. Concrete example already hit:
   `GetEntries` passed a `DateTimeKind.Unspecified` value, which Npgsql *rejects*
   against a `timestamptz` column — InMemory accepted it, real Postgres would not.
   InMemory also won't enforce real unique constraints, migrations, or LINQ-translation
   limits.
2. **External calls (`INutritionEstimator` → DeepSeek).** The AI add-methods make
   outbound HTTP we'll want to test against a stub/recorded server (capability flags,
   timeouts, malformed responses, the text-only vs multimodal split) — without hitting
   the real provider or spending tokens in CI.

**Shape (keep it thin — a second tier, not a rewrite):**

- A separate `Api.IntegrationTests` project (or a `[Trait("Category","Integration")]`
  filter) so local `dotnet test` stays fast and these run as their own CI step.
- Real Postgres via **Testcontainers**, or just point at the `docker compose` Postgres
  the deploy workflow already has up for the migration step.
- External HTTP via a stub server (e.g. WireMock.Net) or recorded fixtures behind the
  `INutritionEstimator` abstraction — never the live provider.
- **First regression test:** `GET entries?from=&to=` round-trips a `timestamptz` range
  on real Postgres (includes an in-window instant, excludes an out-of-window one). This
  is the exact case InMemory missed.

## InternalsVisibleTo convention

To test `internal` members without exposing them publicly, the `Api` project declares:

```xml
<InternalsVisibleTo Include="Api.Tests" />
```

This lets the test project call `internal` methods directly (e.g.
`BackupService.ParseInterval`, `ReleaseNotifier.BuildEmail`) while keeping them
hidden from external consumers.

## CI gate

The `test` job in `.github/workflows/deploy.yml` runs on every push to `main` and
`release`. Both `build` and `deploy` are gated behind it (`build` → `needs: test`,
`deploy` → `needs: build`), so a red suite blocks the image from being built or deployed.
