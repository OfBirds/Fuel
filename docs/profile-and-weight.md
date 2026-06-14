# Profile, weight & meal-pause (Phase 1) — design

> Status: **spec, not yet built** (Phase 1, after the Phase 0 catalogue/logging
> foundation). Adds the user profile, the weight register, the metabolism/BMI
> readout, and the meal-pause warning.

## Goal
Give logging a personalised frame: a **profile** captured at first login that
drives **BMI** and a **metabolism** readout (shown in Settings), a **weight
register** that tracks weigh-ins over time, and a **meal-pause** warning when the
user logs a meal too soon after the last one.

## First-login onboarding
On first login (no profile yet), collect:
- **Height**, **Sex**, **Constitution** (body frame — see below), **Year of birth**.
- **Starting weight** (seeds the weight register).
- **Daily calorie goal** — a **typed number** (the profile informs it, doesn't set
  it).

## Data model
### `UserProfile` (or fields on `User`)
`Height`, `Sex`, `Constitution` (`Small` | `Medium` | `Large`), `YearOfBirth`,
`DailyCalorieGoal` (typed; from Phase 0), `MealPauseHours`, `MealPauseScope`,
`ShowMacros` (default `false`).

### `WeightEntry`
`Id`, `UserId`, `Weight`, `RecordedAtUtc`. First login seeds the starting weight;
the register is the ordered history.

## Metabolism & BMI (computed, shown in Settings — not stored)
- **BMR/TDEE → Mifflin–St Jeor** (the modern, most-accurate standard; takes
  weight, height, age, sex — **not** frame):
  - Men: `10·kg + 6.25·cm − 5·age + 5`
  - Women: `10·kg + 6.25·cm − 5·age − 161`
  - TDEE = BMR × an activity factor (sedentary…very active).
- **BMI** = `weight / height²`.
- **Constitution (body frame) does NOT feed BMR.** It refines **ideal/healthy
  weight ranges + BMI interpretation** (small frame → lower end, large → upper
  end).

### Constitution / frame size — friendly chooser
Frame size comes from the **height ÷ wrist-circumference ratio** `r`
(unit-independent — same unit top & bottom):

| | Small | Medium | Large |
|---|---|---|---|
| Men | `r > 10.4` | `9.6–10.4` | `r < 9.6` |
| Women | `r > 11` | `10.1–11.0` | `r < 10.1` |

UI: a **named Small / Medium / Large** chooser (don't force a measurement), with
an optional **"help me decide"** that takes wrist circumference + the stored
height and computes `r`.

## Weight register page
A simple list of weigh-ins (newest first). Each row:
- the weight + date,
- a **% change vs the adjacent (consecutive) entry** with an **up/down arrow**, or
  an **"x"/dash if unchanged**,
- **delete** (removes the row from the DB) behind a **confirmation**.
An "add weigh-in" affordance appends a new `WeightEntry` (default date = now,
editable).

## Meal-pause warning
- Settings value **`MealPauseHours`** (the "big meal pause"), plus its **scope**
  (`MealPauseScope`) — configured together when the user sets the pause (e.g. warn
  between all intakes, or only between non-snack meals).
- On the entry screen, if the new entry's `IntakeAtUtc` is within `MealPauseHours`
  of the previous in-scope intake, **show a warning** — warn, don't block.

## API
- `GET/PUT /api/user/{userId}/profile` — profile fields + `MealPauseHours`/scope +
  `ShowMacros`.
- `GET /api/user/{userId}/metabolism` — computed BMR/TDEE/BMI + ideal-weight range
  (or compute client-side from the profile; pick one and be consistent).
- `GET /api/user/{userId}/weights`, `POST …/weights`, `DELETE …/weights/{id}`.

## Frontend
- **Onboarding** flow gated on "no profile yet".
- **Settings** — profile fields, the **Show-macros toggle** (gates macro display
  app-wide), meal-pause hours + scope, and the read-only metabolism/BMI block.
- **Weight register page** — the list described above.

## Tests
- **Backend:** Mifflin–St Jeor for both sexes; BMI; frame classification at the
  `r` thresholds (boundaries); weight CRUD; consecutive-delta computation;
  meal-pause boundary (just inside/outside the window, scope honoured).
- **Frontend:** onboarding captures profile + starting weight; show-macros toggle
  flips display; weight rows render delta + arrow / x; delete confirms.
