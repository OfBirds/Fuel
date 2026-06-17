#!/usr/bin/env python3
"""Seed the local Fuel backend with a known demo account + realistic data.

Idempotent: re-running resets the demo user's entries/weights to the canonical
set and reuses existing catalogue foods by name (the catalogue is global/shared),
so repeated runs don't pile up duplicates.

Talks to the running backend over HTTP (default :5200) — the API enforces the
real validation/ownership rules, so the seeded data is exactly what the app would
accept. Requires Postgres + backend up (see the project-startup skill).

Prints a final SEED SUMMARY block (credentials + data counts) for the caller to relay.
"""
import json, os, sys, urllib.request, urllib.error
from datetime import datetime, timedelta, timezone

BASE = os.environ.get("FUEL_API", "http://localhost:5200")
EMAIL = os.environ.get("SEED_EMAIL", "demo@fuel.local")
PASSWORD = os.environ.get("SEED_PASSWORD", "Demo1234!")  # meets policy: letter+number+special, >=8


def req(method, path, body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(BASE + path, data=data, method=method)
    r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=15) as resp:
            raw = resp.read().decode()
            return resp.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
    except urllib.error.URLError as e:
        print(f"ERROR: cannot reach backend at {BASE} ({e}).\n"
              f"Start it first (project-startup skill): backend must answer /api/version.",
              file=sys.stderr)
        sys.exit(2)


def z(dt):
    """UTC ISO-8601 with a trailing 'Z'. A '+00:00' offset would bind as Kind=Local
    and Npgsql rejects that against the timestamptz columns — always send 'Z'."""
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.000Z")


# --- auth: register, or reset+login if the account already exists ---------------
st, body = req("POST", "/api/auth/register", {"email": EMAIL, "password": PASSWORD})
created = st == 200
if not created:
    req("POST", "/api/auth/reset-password", {"email": EMAIL, "newPassword": PASSWORD})
    st, body = req("POST", "/api/auth/login", {"email": EMAIL, "password": PASSWORD})
    if st != 200:
        print(f"ERROR: register and login both failed: {st} {body}", file=sys.stderr)
        sys.exit(1)
uid, tok = body["userId"], body["token"]

# --- reset prior demo data so re-runs stay clean --------------------------------
_, weights = req("GET", f"/api/user/{uid}/weights", token=tok)
for w in (weights or []):
    req("DELETE", f"/api/user/{uid}/weights/{w['id']}", token=tok)
# entries: clear a wide window around today
lo = z(datetime.now(timezone.utc) - timedelta(days=120))
hi = z(datetime.now(timezone.utc) + timedelta(days=1))
_, old = req("GET", f"/api/user/{uid}/entries?from={lo}&to={hi}", token=tok)
for e in (old or []):
    req("DELETE", f"/api/user/{uid}/entries/{e['id']}", token=tok)

# --- profile + goal -------------------------------------------------------------
req("PUT", f"/api/user/{uid}/profile", {
    "height": 178, "sex": "Male", "constitution": "Medium", "yearOfBirth": 1990,
    "activityLevel": "moderate", "mealPauseHours": 3, "mealPauseScope": "non-snack",
    "showMacros": True,
}, tok)
req("PUT", f"/api/user/{uid}/prefs", {"notifyReleases": False, "dailyCalorieGoal": 2200}, tok)

# --- catalogue foods (per-unit nutrition; varied units exercise the unit picker) -
# name: (uom, cal, protein, carbs, fat) per single unit
NUT = {
    "Rolled Oats":    ("g",     0.38, 0.135, 0.66,  0.07),
    "Banana":         ("piece", 105,  1.3,   27,    0.4),
    "Greek Yogurt":   ("g",     0.59, 0.10,  0.036, 0.005),
    "Chicken Breast": ("g",     1.65, 0.31,  0.0,   0.036),
    "Olive Oil":      ("ml",    8.84, 0.0,   0.0,   1.0),
    "Almonds":        ("oz",    164,  6.0,   6.1,   14.2),   # imperial unit
    "Coffee (black)": ("cup",   2,    0.3,   0.0,   0.0),    # other/imperial unit
    "Whole Milk":     ("ml",    0.61, 0.032, 0.048, 0.033),
    "Brown Rice":     ("g",     1.11, 0.026, 0.23,  0.009),
    "Broccoli":       ("g",     0.34, 0.028, 0.066, 0.004),
}
_, cat = req("GET", "/api/foods", token=tok)
ids = {}
for f in (cat or []):
    if f["name"] in NUT and f["name"] not in ids:
        ids[f["name"]] = f["id"]
for name, (uom, cal, p, c, fa) in NUT.items():
    if name in ids:
        continue
    st, fb = req("POST", "/api/foods", {
        "name": name, "defaultUoM": uom, "caloriesPerUnit": cal,
        "proteinPerUnit": p, "carbsPerUnit": c, "fatPerUnit": fa, "ingredients": [],
    }, tok)
    if isinstance(fb, dict):
        ids[name] = fb["id"]

# --- today's entries across meals ----------------------------------------------
PLAN = [
    ("Rolled Oats", 60, "Breakfast", 8), ("Whole Milk", 200, "Breakfast", 8),
    ("Banana", 1, "Breakfast", 8), ("Coffee (black)", 1, "Breakfast", 9),
    ("Chicken Breast", 180, "Lunch", 13), ("Brown Rice", 150, "Lunch", 13),
    ("Broccoli", 120, "Lunch", 13),
    ("Almonds", 1, "Snack", 16), ("Greek Yogurt", 170, "Snack", 16),
    ("Chicken Breast", 150, "Dinner", 19), ("Olive Oil", 10, "Dinner", 19),
    ("Broccoli", 100, "Dinner", 19),
]
entry_count, total_cal = 0, 0
for name, qty, meal, hour in PLAN:
    uom, cal, p, c, fa = NUT[name]
    when = datetime.now(timezone.utc).replace(hour=hour, minute=0, second=0, microsecond=0)
    kcal = round(cal * qty)
    st, _ = req("POST", f"/api/user/{uid}/entries", {
        "foodId": ids[name], "foodName": name, "intakeAtUtc": z(when), "mealType": meal,
        "quantity": qty, "uoM": uom, "calories": kcal,
        "protein": round(p * qty, 1), "carbs": round(c * qty, 1), "fat": round(fa * qty, 1),
    }, tok)
    if st in (200, 201):
        entry_count += 1
        total_cal += kcal

# --- weight history (gentle downward trend) ------------------------------------
now = datetime.now(timezone.utc)
weight_count = 0
for days_ago, kg in [(35, 84.2), (28, 83.6), (21, 83.1), (14, 82.4), (7, 82.0), (0, 81.5)]:
    when = (now - timedelta(days=days_ago)).replace(hour=7, minute=30, second=0, microsecond=0)
    st, _ = req("POST", f"/api/user/{uid}/weights", {"weight": kg, "recordedAtUtc": z(when)}, tok)
    if st in (200, 201):
        weight_count += 1

print("===== SEED SUMMARY =====")
print(f"App:        {BASE.replace('5200', '3000')}  (frontend)")
print(f"Email:      {EMAIL}")
print(f"Password:   {PASSWORD}")
print(f"User ID:    {uid}")
print(f"Account:    {'created' if created else 'existed → password reset, data reseeded'}")
print(f"Data:       {len(ids)} catalogue foods, {entry_count} entries today "
      f"({total_cal} cal vs 2200 goal), {weight_count} weigh-ins (84.2 → 81.5 kg)")
print(f"Toggles:    Show macros ON, Grouped unit picker is a client pref (default ON)")
