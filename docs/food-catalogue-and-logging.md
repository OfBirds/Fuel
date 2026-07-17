# Food catalogue & logging (Phase 0) — design

> Status: **built / live**. This is Fuel's first feature — the foundation
> every later phase attaches to. Phase 0 ships the **manual** add path and the
> screen that the AI paths (Phase 2/3) reuse.

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
  values, ingredients via add-child with inline-define); delete. On ingredient
  rows of existing foods the unit is **read-only** (fixed to the child's
  `DefaultUoM`, same rule as diary logging — change it on the food itself);
  only inline-defined new ingredients choose a unit.
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

> **TODO (per-day goal history):** the day view's calorie ring + "Cal left / Cal
> consumed" figures use the user's **current** `DailyCalorieGoal` for every day, including
> past ones. That makes history wrong once the goal changes — yesterday's ring is computed
> against today's target. Persist the planned (goal) calories **per day** (snapshot the
> goal in effect on each logged day) so the ring and figures are true when scrolling back.
> For another time.

## AI-assist panel, unit-system conversion & reference quantities (Phase 2b/3/5)

### Catalogue AI-assist panel (`FoodAiAssist`)

**`FoodAiAssist`** (`frontend/src/components/FoodAiAssist.tsx`) hosts the shared **`AiInputPanel`** — the same Text/Photo/Barcode control as the diary's "Describe with AI" screen — inside the catalogue's **add-food** dialog. Same backend endpoints (`/api/user/{userId}/estimate/text` and `.../estimate/image`) — no new API. It is **hidden when editing** an existing food: editing is a manual correction, not an estimation task.

The panel never persists on its own. An estimate (its first item — a food description names one food) or a barcode hit **auto-fills** the visible form fields (name, default unit, per-unit calories/macros, converted to the reference basis); the dialog's own **Save Food** button is the only confirmation.

### Unit-system inference + conversion

`frontend/src/lib/units.ts` exports two helpers for switching values between metric and imperial, applied to **system-produced** values only (AI estimates, barcode lookups — never values the user typed or saved):

- **`inferPreferredSystem(foods)`** — takes an array of the user's catalogue foods (each annotated with `usageCount`) and does a usage-weighted vote. Metric units (g, kg, mg, ml, l) vote metric; imperial (oz, lb, fl oz, cup) vote imperial; neutral units (piece, slice, serving, tbsp, tsp) abstain. A tie — or no data at all — defaults to **metric**.
- **`convertToSystem(row, system)`** — converts a single `ConvertibleRow`'s `quantity` + `uom` into the target system using standard factors (oz→g ×28.35, lb→kg ×0.4536, fl oz→ml ×29.57, cup→ml ×240 and their inverses). **Calories and macros are never touched** by this conversion — only the quantity+unit pair changes so the new per-unit label stays consistent.

### Reference-quantity table

The `refQty`/`refLabel` table in the same file defines the **reference basis** wherever per-unit nutrition is displayed:

| Unit(s) | Reference quantity | Grounding |
|---|---|---|
| g, ml | 100 | EU 1169/2011 / Codex Alimentarius per-100 |
| kg, l, oz, lb, cup | 1 | Per-whole-unit |
| mg | 1000 | Per-gram equivalent |
| fl oz | 8 | US FDA RACC (21 CFR 101.9) |
| piece, slice, serving, tbsp, tsp | 1 | Per-countable-unit |

Unknown/legacy units default to a plain "per 1 ⟨unit⟩". The list is small and extensible — add a new entry to the mapping, not a rewrite of the display layer.

### Boundary-conversion rule

Canonical storage is unchanged — `Food.CaloriesPerUnit` and the macro fields stay **per-1-unit** in whatever unit the food was defined in. The catalogue form converts to reference-basis **only at the UI boundary**:

- `startEdit(food)` multiplies the stored per-unit values by `refQty` so the user sees "Calories per 100 g" for a g-based food.
- `saveFood(form)` divides back by `refQty` before writing, restoring canonical storage.

This conversion happens **once on open and once on save** — never on keystroke, to avoid float drift. Backend, entry math, and composites are untouched.

## Tests
- **Backend (xUnit + EF InMemory):** Food CRUD; cycle detection rejects
  self/transitive links; composite rollup; entry create + snapshot; day-total
  sum; UoM default ordering.
- **Frontend (Vitest):** day-view grouping + total-vs-goal; entry add/edit form;
  catalogue define with an inline ingredient.
