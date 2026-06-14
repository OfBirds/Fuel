# Barcode / EAN lookup (Phase 3) — design

> Status: **spec, not yet built**. A grocery-packaging **barcode scan** that
> resolves to an official food definition when one exists. It is a **database
> lookup, not an AI estimate** — its own seam, separate from `INutritionEstimator`
> (`ai-providers.md`). It feeds the same unified entry screen as the AI paths
> (`ai-estimation.md`).

## Goal
Point the camera at an EAN-13/UPC barcode → if the product is known, prefill a
catalogue food with its **official** per-unit nutrition; if not, say so and route the
user to **describe it** (Phase 2) or **photograph it** (Phase 3).

## Two halves
### 1. Decode the barcode (client)
- Decode in-browser from the **local device camera** (same `getUserMedia` +
  **secure-context (HTTPS/localhost)** constraint as photo capture — see
  `ai-estimation.md` §capture).
- The native `BarcodeDetector` API is Chrome/Android-only, so use a cross-browser
  library (e.g. `@zxing/browser` or `html5-qrcode`) over the camera stream.
- Output: the numeric code (EAN-13/UPC-A). Manual entry of the digits is the
  no-camera fallback.

### 2. Resolve the code → food (server)
- Source: **Open Food Facts** — free, global, no API key, barcode-native:
  `GET https://world.openfoodfacts.org/api/v2/product/{barcode}.json`.
- Map its `product_name` + `nutriments` (`energy-kcal_100g`, `proteins_100g`,
  `carbohydrates_100g`, `fat_100g`) → a `Food` with **default unit `g`** and
  per-gram calories/macros (the `_100g` values ÷ 100).
- **Found** → create/prefill the catalogue food (badged "new — added", editable),
  then it lands on the entry screen like any matched food.
- **Not found / missing nutrition** → return a clear miss: *"Couldn't identify this
  product — describe it or take a photo,"* with buttons into the text and photo paths.

## Its own seam
```
IBarcodeFoodLookup
  Task<BarcodeMatch?> LookupAsync(string barcode)   // null = not found

BarcodeMatch { Name, CaloriesPerGram, ProteinPerGram, CarbsPerGram, FatPerGram, Source }
```
- First (and default) implementation: `OpenFoodFactsLookup`.
- **Cache hits into our own catalogue** — a resolved barcode is stored as a `Food`
  (with its EAN), so repeat scans are instant and work offline; a `Barcode` field on
  `Food` (nullable, unique) keys the cache.

## Configuration (flat env vars, like `AI_*`)
| Key | Purpose |
|---|---|
| `BARCODE_ENABLED` | off → hide the scan affordance |
| `BARCODE_BASE_URL` | product DB endpoint (default Open Food Facts) |
| `BARCODE_TIMEOUT_SECONDS` | guard for the external call |

No secret/API key for Open Food Facts. If a future source needs one, it follows the
`AI_KEY_*` handling (`/opt/fuel/.env.*`, never committed).

## Resilience
External call → time-box (`BARCODE_TIMEOUT_SECONDS`); on timeout/error or a not-found,
**never block** — surface the "describe or photograph it" fallback. Log via Serilog
(→ Seq) with the code + hit/miss (no PII).

## Out of scope here
Barcode generation, loyalty/price data, non-food products. Camera plumbing is shared
with `ai-estimation.md`. Catalogue model (`Food`, snapshots) is in
`food-catalogue-and-logging.md`.

## Tests
- **Backend:** with a **fake `IBarcodeFoodLookup`**, a hit creates/returns a catalogue
  `Food` with correct per-gram values; a miss returns the describe/photo fallback; the
  `Food.Barcode` cache short-circuits a second lookup; timeout → fallback.
- **Frontend:** a decoded code calls the lookup; hit prefills the entry screen; miss
  shows the fallback with text/photo buttons; manual digit entry works without a camera.
