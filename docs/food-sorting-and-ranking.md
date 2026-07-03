# Food catalogue — sorting & ranking

> Status: **built / live.** Shipped after Phase 2 (AI text) and Phase 3
> (AI photo). The catalogue is intentionally **global / shared**
> (no `UserId` on `Food`) so a **household co-builds and co-edits one set of
> foods** — nobody re-enters what a family member already added. This feature makes
> that shared, growing list navigable.

## Manual priority — "ponder"
A **per-user** integer **`Ponder`** for a food — each household member ranks the shared
catalogue their own way (the food *data* is shared; the *ordering* is personal).
- **Default `100`. Sort ascending — lower = stronger** (ponder 20 outranks ponder 40).
- **Positive integers only (natural numbers): floor `0`, no negatives.** The `100`
  default leaves headroom in both directions — promote a favourite by lowering it toward
  `0`, demote an item by raising it well above `100`.
- The model is **demotion/promotion around a mid-point**: every food starts at `100`;
  *lower* its ponder to promote (favourites, staples), *raise* it to push it down the list
  (rarely-used items, duplicates, joke entries).
- **Decided:** no negative ponder — `0` is the floor. Negatives looked odd; a `100`
  default with a `0` floor keeps values to plain natural numbers while staying expressive
  both ways.

### Storage — a per-user join table
`Food` is intentionally global (no `UserId`), so ponder **can't be a column on `Food`**.
Instead a small table holds only the per-user overrides:

- **`UserFoodPriority(UserId, FoodId, Ponder)`**, composite PK `(UserId, FoodId)`.
- **Default `100` = absence of a row.** Only rows that deviate from `100` are stored — no
  backfill, no row-per-(user×food) explosion. Setting a ponder is an **upsert**; setting
  it back to `100` can simply delete the row.
- Priority sort: `LEFT JOIN UserFoodPriority fp ON fp.FoodId = f.Id AND fp.UserId = me`,
  then `ORDER BY COALESCE(fp.Ponder, 100) ASC, f.Name`.

## Selectable sort modes (food picker + catalogue page)
When choosing foods for **meals** or **ingredients**, and on the catalogue page, the
user picks the order:

| Mode | Order |
|---|---|
| **Priority** (default) | `Ponder` asc, then name |
| **Alphabetical** | name A–Z |
| **Most-used** | usage count desc |
| **Recent** | last-used desc |

`Ponder` may also serve as a tiebreaker within the other modes (decide when built).

## No new tracking table — derive from `FoodEntry`
Usage and recency are **already in the data**: `FoodEntry(UserId, FoodId, IntakeAtUtc)`.
- **Most-used** = `COUNT(FoodEntry WHERE FoodId = f AND UserId = me)`
- **Recent** = `MAX(FoodEntry.IntakeAtUtc WHERE FoodId = f AND UserId = me)`

So the **only schema change is the `UserFoodPriority` table** (plus supporting indexes).
No usage counters to maintain, nothing to keep in sync.

- **Decided — scope of Most-used / Recent: both per-user.** Each is filtered
  `AND UserId = me` — Most-used = "what *I* log most", Recent = "what *I* just logged".
  Keeps ranking personal rather than household-wide.

## Where it shows up
- The manual food picker on the unified entry screen (choosing a food for a meal).
- The ingredient picker when defining a composite food.
- The catalogue page (browse/manage).

## Tests
The sort/aggregation SQL — `Ponder asc`, frequency `COUNT`, recency `MAX`, and the
per-user vs global filter — is exactly the provider-specific query shape the **next**
step (the real-Postgres integration tier) should cover, alongside the AI calls. Keep
the picker's sort-mode switch covered by a frontend unit test.
