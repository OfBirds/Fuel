# AI calorie estimation — text & photo (Phase 2 & 3) — design

> Status: **spec, not yet built**. Phase 2 = estimate from a **typed description**;
> Phase 3 = estimate from a **photo** (+ a **barcode** fast-path, see
> `barcode-lookup.md`). Both reuse the unified entry screen from Phase 0 and the
> provider abstraction in `ai-providers.md`.

## Goal
Let the user log food without doing the lookup: **type** a description, **snap a
photo**, or **scan a barcode**, and get a structured, editable result they confirm
before anything is saved. The AI never writes to history directly.

## The unified review screen (multi-item)
A single input can describe **several foods** ("chicken 1 kg with 100 g green
salad", or a plate with rice + beans + chicken). So the estimate is a **list of
line-items**, and the entry screen is a **multi-row review**:
- Each row = one food: name, quantity, unit, calories, macros, confidence.
- Every field is **editable**; rows can be **deleted**; low-confidence rows are
  flagged for attention.
- On save, **each row becomes its own `FoodEntry`** for the chosen meal/time, each
  referencing its own catalogue `Food`.
- Word order and phrasing are the model's job — we only consume the structured list.

## Phase 2 — text
1. User types a description and hits **Estimate**.
2. Backend calls `INutritionEstimator.EstimateFromTextAsync(description, notes)`
   (`notes` empty on the first pass; provider chosen by `AI_PROVIDER`, DeepSeek first).
3. The returned items populate the review screen. `Source = AiText`, per-item
   `AiConfidence` recorded.
4. New foods are **defined into the catalogue first** (see §"new food" below), then
   the `FoodEntry` references them.

### Units & conversion
When an item resolves to an existing catalogue food whose **default unit differs**
from the stated one:
- **Same dimension** (kg↔g, l↔ml, tbsp↔ml, …) → convert with a deterministic
  table. Reliable, no AI needed.
- **Cross-dimension / ambiguous** (food defined per `g`, user said "2 cups") →
  there's no exact conversion without density, so we **ask the model to express the
  amount in the food's default unit** (it knows "2 cups cooked rice ≈ 370 g"). The
  matched food's default unit is passed into the prompt for this.
- **Still impossible / low confidence** → fall back to the model's **absolute**
  calorie/mac. figure for the stated amount, store it as the entry's snapshot, and
  flag the row **"needs review."** Because entries already snapshot their nutrition
  (Phase 0), an imperfect per-unit link never blocks logging — the user edits freely.

## Refine loop — clarifications after the first result
Applies to **both** text and photo. After the first estimate renders, the screen
shows a **"add a note / clarify"** box. The user can correct what the model missed
("the spread is peanut butter on a slice of bread; the beans are right") and hit
**Refine**:
- The note is appended to `notes` and the **same estimator call is re-issued** with
  the original input + the accumulated notes. The model returns an updated item list.
- The **server stays stateless** — the client owns the conversation thread.
- The user can refine repeatedly, then confirm/edit as usual. Good guesses from the
  first pass (e.g. the beans) carry through; the clarifications sharpen the rest.

## Phase 3 — photo
1. The user provides an image two ways (see §capture): **live camera** or **upload**.
2. Backend hands the bytes to `EstimateFromImageAsync(bytes, contentType, notes)`
   (DeepSeek supports image input).
3. Same multi-row review + **refine loop** as text; `Source = AiPhoto`.
4. If the photo result is wrong beyond refining, the user can re-type for AI or fall
   back to manual.

### Image lifetime — never persisted, in-memory during review
The photo is **never written to the DB or disk**. It is held **in browser memory for
the duration of the active review/refine session only** (it must be re-sent with each
refine turn, since refining needs the pixels), and dropped when the user saves or
leaves the screen. "Estimate-then-discard" = discard when the review ends.

### Capture — local device camera + upload
The camera is the **local machine's own camera** accessed in-browser (laptop webcam
on desktop, the phone camera on mobile) — the same `getUserMedia` mechanism Teams/Meet
use. **Not** a network/IP camera.
- **Primary:** `navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } })`
  → `<video>` preview → snapshot to `<canvas>` → blob.
- **Fallback (always available):** `<input type="file" accept="image/*"
  capture="environment">` — opens the rear camera on mobile, a file/gallery picker
  on desktop. Also covers "upload an existing photo".
- ⚠️ **Secure-context requirement:** browsers only grant `getUserMedia` over **HTTPS
  or `localhost`** (exactly why Teams-in-browser is HTTPS). So live capture works on
  `localhost` (dev) and in **prod once it has TLS**, but **not** over plain-HTTP LAN
  staging — there the upload fallback is used. This ties live capture (and barcode
  scanning) to the prod reverse-proxy/TLS task (`infrastructure.md` §Deferred).

## Barcode fast-path (Phase 3)
Scanning an EAN/UPC off grocery packaging resolves to an **official** product
definition when one exists, else tells the user to describe or photograph it. It is a
**database lookup, not an AI estimate**, and lives in its own seam — full design in
**`barcode-lookup.md`**. (Same camera/secure-context constraints as photo capture.)

## New food → catalogue, with a visible "what I added"
When the AI (or barcode) names a food not in the catalogue, it is **created there
first**, then referenced by the entry. In the review the new row is **badged
"new — added to catalogue"** showing the assigned values (cal/unit, macros, source),
with an **edit-in-catalogue** affordance so the user can fix the canonical definition
on the spot. Existing foods are reused (`MatchedFoodId`). Fuzzy-match / dedup / "did
you mean" is a **later** optimization, not now.

## Cost & resilience
External and slow → see `ai-providers.md` §Resilience (time-box, one retry,
manual fallback, schema validation, disabled-when-off, Seq logging). Photos are
never logged or persisted.

## Tests
- **Backend:** with a **fake `INutritionEstimator`**, the controller maps a
  multi-item estimate onto prefilled rows; new-food rows create catalogue `Food`s
  then entries; same-dimension unit conversion is exact; cross-dimension falls back
  to absolute calories + "needs review"; a refine call re-issues with accumulated
  `notes`; timeout/failure yields the manual-fallback response; `Source`/
  `AiConfidence` set correctly. (No live provider calls in tests.)
- **Frontend:** "Estimate" populates multiple rows; per-row edit/delete; user edits
  override AI values; the refine box appends a note and re-requests; capture falls
  back to file input when `getUserMedia` is unavailable; fallback message on error.
