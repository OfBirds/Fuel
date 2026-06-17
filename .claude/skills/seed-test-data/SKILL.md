---
name: seed-test-data
description: Seed the local Fuel database with a known demo account and realistic data (profile, catalogue foods, a full day of meals, weight history) for manual testing. Use when asked to "prepare for tests", set up test/demo data, or create a known login to poke at the running app.
---

# Seed Fuel test data

Creates (or resets) a **known demo account** with realistic data so the app has
something to look at: a completed profile, a catalogue of foods with varied units,
a full day of logged meals across breakfast/lunch/dinner/snack, and a weight
history with a downward trend.

The seeder talks to the **running backend over HTTP** (not raw SQL), so everything
goes through the real validation + ownership rules — the data is exactly what the
app would accept.

## Prerequisite: the backend must be up

The script needs Postgres + the backend answering on **:5200**. If it isn't,
start it first with the **project-startup** skill (Postgres → backend; the
frontend too if you want to log in and look). Quick check:

```bash
curl -sf http://localhost:5200/api/version && echo OK
```

If that fails, run project-startup first, then come back.

## Run it

```bash
python3 .claude/skills/seed-test-data/seed.py
```

It prints a `===== SEED SUMMARY =====` block at the end. **Relay that summary to
the user** — specifically the **email, password, and a one-line note on what data
it has** (that's the whole point of the skill).

### Idempotent

Safe to re-run. It resets the demo user's entries/weights to the canonical set
before reseeding and reuses existing catalogue foods by name (the catalogue is
global/shared), so repeated runs don't pile up duplicates. If the account already
exists, its password is reset to the known value.

### Overrides (env vars, optional)

- `SEED_EMAIL` / `SEED_PASSWORD` — use a different login (password must satisfy the
  policy: ≥8 chars, a letter, a number, and a special char).
- `FUEL_API` — backend base URL (default `http://localhost:5200`).

## What gets created (defaults)

- **Login:** `demo@fuel.local` / `Demo1234!`
- **Profile:** 178 cm, male, medium frame, b.1990, moderate activity, 3 h meal-pause
  (non-snack), **show-macros ON**; daily goal 2200 cal.
- **Catalogue:** 10 foods with full macros and **varied units** — `g`, `ml`,
  `piece`, plus `oz` (Almonds) and `cup` (Coffee) so the grouped unit picker has
  metric/imperial/other entries to show.
- **Today's log:** 12 entries (~1391 cal) across all four meal sections.
- **Weight history:** 6 weigh-ins over 5 weeks (84.2 → 81.5 kg) so the weight page
  shows per-row % deltas + arrows.

## Gotcha baked into the seeder

Client-supplied timestamps are sent as UTC ISO with a trailing **`Z`**. A `+00:00`
offset binds as `DateTimeKind.Local`, which Npgsql rejects against the `timestamptz`
columns (500 on entry/weight create). The real frontend always sends `Z`; the
seeder does too. Keep it that way if you extend the data.
