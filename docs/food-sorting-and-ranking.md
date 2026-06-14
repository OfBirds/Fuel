# Food catalogue — sorting & ranking (planned)

> Status: **spec, not yet built.** Sequencing: after Phase 2 (AI text) and Phase 3
> (AI photo), and **before** the integration-test / containers tier (see
> `docs/testing.md` §Planned). The catalogue is intentionally **global / shared**
> (no `UserId` on `Food`) so a homelab **household co-builds and co-edits one set of
> foods** — nobody re-enters what a family member already added. This feature makes
> that shared, growing list navigable.

## Manual priority — "ponder"
A per-food integer **`Ponder`** on `Food` (global, shared, editable on add *and* edit).
- **Default `0`. Sort ascending — lower = stronger** (ponder 20 outranks ponder 40).
- The model is **demotion**: every food starts prime (0); you *raise* a food's ponder
  to push it down the list (rarely-used items, duplicates, joke entries). Favourites
  are simply the ones left low.
- **Open question:** allow **negative** ponder (to *promote* a few favourites above the
  default crowd), or keep `0` as the floor (demote-only)? Leaning: allow negatives —
  cheap, and it makes "favourite" expressive rather than only "demote everything else."

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
- **Most-used** = `COUNT(FoodEntry WHERE FoodId = f [AND UserId = me])`
- **Recent** = `MAX(FoodEntry.IntakeAtUtc WHERE FoodId = f [AND UserId = me])`

So the **only schema change is the `Ponder` column** (plus supporting indexes). No
counters to maintain, nothing to keep in sync.

- **Open question — scope of Most-used / Recent: per-user or global/household?** One-line
  filter difference. Leaning: **Recent = per-user** (recency is personal — "what *I*
  just logged"), **Most-used = global** (surfaces the household staples). Trivial to do
  either or both.

## Where it shows up
- The manual food picker on the unified entry screen (choosing a food for a meal).
- The ingredient picker when defining a composite food.
- The catalogue page (browse/manage).

## Tests
The sort/aggregation SQL — `Ponder asc`, frequency `COUNT`, recency `MAX`, and the
per-user vs global filter — is exactly the provider-specific query shape the **next**
step (the real-Postgres integration tier) should cover, alongside the AI calls. Keep
the picker's sort-mode switch covered by a frontend unit test.
