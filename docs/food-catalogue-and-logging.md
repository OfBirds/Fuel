# Food catalogue & logging (Phase 0) — design

> Status: **spec, not yet built**. This is Fuel's first feature — the foundation
> every later phase attaches to. Phase 0 ships the **manual** add path and the
> screen that the AI paths (Phase 2/3) will reuse. See the build plan on the NAS
> (`coding/Fuel/app-plan.md`) for phase ordering.

## Goal

Log what you eat against a **daily calorie goal**. A food is **defined once in a
catalogue** and reused; log entries reference it. There are three ways to add a
food — manual, typed (AI), photo (AI) — all converging on **one editable entry
screen**. Phase 0 delivers that screen's **manual** path, the catalogue, and the
meal-sectioned day view. Calories are the headline; macros are estimated/stored
but their display is gated by a Settings toggle (Phase 1).

## Data model (EF Core + a migration `AddFoodCatalogueAndEntries`)

### `Food` (catalogue definition — canonical)
| Field | Notes |
|---|---|
| `Id` | Guid PK |
| `Name` | required |
| `DefaultUoM` | one unit chosen at definition (`g`, `ml`, `piece`, …) |
| `CaloriesPerUnit` | required |
| `ProteinPerUnit` / `CarbsPerUnit` / `FatPerUnit` | nullable; stored even if hidden |
| `IconRef` | nullable; AI-generated later (Phase 3) |
| `CreatedAtUtc` / `UpdatedAtUtc` | audit |

A food can be standalone **and** an ingredient of other foods.

### `FoodIngredient` (self-referential composition)
`ParentFoodId`, `ChildFoodId`, `Quantity`, `UoM`. A composite food = parent + its
child rows. Children are existing catalogue foods or **defined inline** at link
time. **Cycle detection is required:** reject any link that would make a food
contain itself directly or transitively. Composite nutrition may **roll up** from
children (Σ child per-unit × normalized qty) but stays **editable/overridable** on
the parent.

### `FoodEntry` (a logged item)
| Field | Notes |
|---|---|
| `Id` | Guid PK |
| `UserId` | owner |
| `FoodId` | the catalogue food logged |
| `IntakeAtUtc` | **confirm/editable**, defaults to now |
| `MealType` | `Breakfast` \| `Lunch` \| `Dinner` \| `Snack` |
| `Quantity` + `UoM` | amount eaten |
| `Calories` + macros | **snapshotted at log time** so later catalogue edits don't rewrite history |
| `Source` | `Manual` \| `AiText` \| `AiPhoto` (Phase 0 = `Manual`) |
| `AiConfidence` | nullable (AI phases) |

Add `DbSet`s + `OnModelCreating` config to `AppDbContext`. **Daily goal:** add
`DailyCalorieGoal` to the user now (a typed number) so the day view has a target;
Phase 1's profile builds around it.

## Units of measure
Small extensible set (`g`, `ml`, `piece`). One `DefaultUoM` per food, chosen at
definition (not a long list). In the manual entry picker, **`g` (solids) and `ml`
(liquids) sort first**.

## API (all under `/api`)
- **Catalogue:** `GET /api/foods` (list/search), `GET /api/foods/{id}`,
  `POST /api/foods` (define; body may include ingredient links + inline child
  definitions), `PUT /api/foods/{id}`, `DELETE /api/foods/{id}`.
- **Entries:** `GET /api/entries?date=YYYY-MM-DD` (the user's day),
  `POST /api/entries`, `PUT /api/entries/{id}`, `DELETE /api/entries/{id}`.
- **Goal:** `GET/PUT /api/user/{userId}/goal` (or fold into the existing prefs
  endpoint).

> Auth is still the demo placeholder — routes carry `{userId}` like the existing
> `UserController`. Keep that pattern; real `[Authorize]` comes with the auth
> hardening, not this feature.

## Frontend
- **Day view (`HomePage`)** — sections **Breakfast / Lunch / Dinner / Snacks**,
  each listing its entries (qty · food · calories, + macros when `ShowMacros`).
  Header: date navigation + **today's total vs goal** (progress). Each section has
  an "add" that opens the entry screen pre-set to that meal.
- **Add/edit entry screen** (the unified target of all three add methods) — pick a
  catalogue food (search) or **define one inline**; quantity + UoM (g/ml first);
  meal type; **intake time** (editable, default now); shows computed
  calories/macros, all editable; save → snapshot `FoodEntry`.
- **Catalogue page** — list foods; define/edit (name, default UoM, per-unit
  values, ingredients via add-child with inline-define); delete.
- New client prefs go through `src/lib/storage.ts` (e.g. last-used meal), not raw
  `localStorage`.

## Rules
- Today's total is **derived** (sum over the user's local day) — no stored
  aggregate.
- Entry nutrition is **snapshotted** at log time.
- Ingredient links run **cycle detection**.
- Deleting a referenced food: entries keep their snapshot and survive; a composite
  parent must drop the child link first (or cascade with confirmation).

## Out of scope (later phases)
AI text/photo logging (Phase 2/3), the macro-display toggle UI and profile-derived
goals (Phase 1), per-food icons (Phase 3), catalogue-lookup optimization (later).

## Tests
- **Backend (xUnit + EF InMemory):** Food CRUD; cycle detection rejects
  self/transitive links; composite rollup; entry create + snapshot; day-total
  sum; UoM default ordering.
- **Frontend (Vitest):** day-view grouping + total-vs-goal; entry add/edit form;
  catalogue define with an inline ingredient.
