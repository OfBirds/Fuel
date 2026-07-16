# Testing

> How to run the test suites and what each one covers.

## Harness layout

| Suite | Location | Framework | Type |
|-------|----------|-----------|------|
| Backend (unit) | `backend/Api.Tests/` | xUnit + EF Core InMemory | Unit + service-level integration |
| Backend (integration) | `backend/Api.IntegrationTests/` | xUnit + Testcontainers (Postgres) | Real-database integration |
| Frontend | `frontend/src/**/*.test.{ts,tsx}` | Vitest + Testing Library | Unit + component render |

Both stacks run in CI on every PR targeting `main` and every push to `main` before the
image is built or deployed (`test` job in `.github/workflows/validate.yml` and
`release.yml`).

## Running tests

### Backend

```bash
# From the repo root:
dotnet test backend/Fuel.slnx -c Release
```

The solution file (`backend/Fuel.slnx`) references `Api`, `Api.Tests`, and
`Api.IntegrationTests`. A single `dotnet test` discovers everything. EF Core
InMemory is used for unit tests — no real PostgreSQL needed for `Api.Tests`.
`Api.IntegrationTests` spins up a real Postgres container via Testcontainers
(Docker required).

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

### Integration — `Api.IntegrationTests`

| File | What it tests |
|------|---------------|
| `EntryIntegrationTests.cs` | `GET entries?from=&to=` timestamptz range round-trip on real Postgres (the exact case InMemory missed); ordering within a window. Inputs are Utc-kind and the in-window entry sits strictly inside the range, so the result is timezone-independent. |
| `FoodIntegrationTests.cs` | Composite-food `IngredientCount` / `IsComposite`; the three per-user **sort modes** (`priority` / `most-used` / `recent`) — the `GroupJoin` + aggregate + null-coalescing `OrderBy` SQL InMemory translates differently from Npgsql; and **cycle detection** (`FoodService.WouldCreateCycle`) over a real ingredient graph. |
| `EstimatorIntegrationTests.cs` | Full HTTP round-trips for OpenAI and Anthropic estimators via WireMock.Net: successful text/image parsing, auth headers, thinking-block skipping, malformed inner JSON → `AiUnavailableException`, server errors → `HttpRequestException`, empty-item filtering, connection refusal. |
| `EstimatorChainIntegrationTests.cs` | The ordered multi-provider registry (`EstimatorChain`) over real HTTP: fall-through when the first provider is unreachable, `Order` precedence, all-providers-fail → `AiUnavailableException`, capability filtering (text-only providers reject a vision request without calling upstream), and disabled-provider skipping. |
| `PostgresFixture.cs` | xUnit collection fixture: spins one Testcontainers Postgres container, applies migrations, yields fresh `AppDbContext` per test. `ResetAsync()` truncates all data tables so a test can assert on whole-table counts/ordering without cross-test pollution (the collection runs sequentially). |
| `WireMockFixture.cs` | xUnit collection fixture: starts one WireMock.Net server on a random port; provides a convenience `Connection()` helper. Estimator test classes call `Server.Reset()` in their constructor so per-test mappings don't accumulate on the shared server. |

## Real-dependency integration tier

Now built as `backend/Api.IntegrationTests/`. The two reasons it exists:

1. **EF InMemory is not Postgres.** It silently ignores provider-specific rules, so a
   green unit suite can still ship a persistence bug. Concrete examples already hit:
   `GetEntries` passed a `DateTimeKind.Unspecified` value, which Npgsql *rejects*
   against a `timestamptz` column (InMemory accepted it); `ToListItem(…)` inside a
   LINQ `Select` projection broke ingredient-count translation on real Npgsql while
   InMemory hid the regression.
2. **External calls (`INutritionEstimator` → AI providers).** The AI add-methods make
   outbound HTTP that should be tested against a stub/recorded server (capability
   flags, timeouts, malformed responses, the text-only vs multimodal split) —
   without hitting the real provider or spending tokens in CI. (WireMock.Net is
   included as a dependency for this.)

Shape:
- Separate `Api.IntegrationTests` project in the same solution — `dotnet test`
  discovers it alongside unit tests.
- Real Postgres via **Testcontainers** (`postgres:15-alpine`).
- External HTTP stubs via **WireMock.Net** (estimator + provider-chain tests).
- No `[Trait]` filter by default — the CI runner has Docker and runs both tiers.

## InternalsVisibleTo convention

To test `internal` members without exposing them publicly, the `Api` project declares:

```xml
<InternalsVisibleTo Include="Api.Tests" />
```

This lets the test project call `internal` methods directly (e.g.
`BackupService.ParseInterval`, `ReleaseNotifier.BuildEmail`) while keeping them
hidden from external consumers.

## CI gate

The `test` job runs on every PR targeting `main` (`.github/workflows/validate.yml`)
and every push to `main` (`.github/workflows/release.yml`). In both, `build` and the
deploy job are gated behind it (`build` → `needs: test`, deploy → `needs: build`), so
a red suite blocks the image from being built or deployed.
