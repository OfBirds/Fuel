# AI calorie estimation — text & photo (Phase 2 & 3) — design

> Status: Phase 2 (**typed description**) and Phase 3 **photo** are **built** — text
> via DeepSeek, photo via Claude (vision), behind the composite estimator. The
> **barcode** fast-path (see `barcode-lookup.md`) is also built. Photo/barcode capture uses a
> native file input (the OS "Camera or Photo Library" chooser on mobile) — no in-browser
> live-camera stream, so no HTTPS/secure-context requirement (see §Capture). All paths reuse the unified entry screen from
> Phase 0 and the provider abstraction in `ai-providers.md`.

## Goal
Let the user log food without doing the lookup: **type** a description, **snap a
photo**, or **scan a barcode**, and get a structured, editable result they confirm
before anything is saved. The AI never writes to history directly.

## Language support
The AI prompt instructs the model to return **English food names** regardless of
input language, so they can be matched against the shared (English) food catalogue.

- **English:** works best (~100% match rate). Just describe what you ate naturally.
- **Latin-alphabet languages** (German, French, Serbian, Polish, etc.): usually fine.
  Tested across Serbian, German, and Russian (Latin-transcribed); the model
  understands the food correctly and translates the name to English most of the time.
  German → English is near-perfect; Slavic languages are ~80% reliable.
- **Cyrillic (Russian, Ukrainian, etc.):** the model understands the food but often
  returns the name in Cyrillic, missing the catalogue match. If you must use Cyrillic,
  expect to edit the names manually after estimation.
- **Latin-transcribed Cyrillic** (writing Russian with English letters, e.g.
  "kuraga" for курага): the model *understands* it but name translation is hit-or-miss
  — regional/dialect words like "kajmak" or "kuraga" may stay untranslated. Refine
  with a clarification note, or edit the name directly in the review row.
- **When a name isn't matched,** the row appears with an orange left border and an
  "edit the name" hint. Just type the English name — the food is created in your
  catalogue on save and will match next time.

An info tooltip next to the description box summarizes this for the user.

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
2. Backend calls `EstimatorChain.EstimateFromTextAsync(description, notes)` (`notes` empty
   on the first pass; the chain tries text providers in `order`, falling through on failure
   — see `ai-providers.md`).
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

> **Catalogue reuse.** These same estimate endpoints (text and image) also power the **AI-assist panel** in the catalogue's add/edit food dialog — see the *AI-assist panel* section in [`food-catalogue-and-logging.md`](food-catalogue-and-logging.md) for details.

## Phase 3 — photo
1. The user provides an image via a native file input (see §capture): the OS **camera or photo library** on mobile, a file picker on desktop.
2. Backend hands the bytes to `EstimateFromImageAsync(bytes, contentType, notes)`
   (routed to Claude — the vision provider; DeepSeek is text-only, see `ai-providers.md`).
3. Same multi-row review + **refine loop** as text; `Source = AiPhoto`.
4. If the photo result is wrong beyond refining, the user can re-type for AI or fall
   back to manual.

### Image lifetime — never persisted, in-memory during review
The photo is **never written to the DB or disk**. It is held **in browser memory for
the duration of the active review/refine session only** (it must be re-sent with each
refine turn, since refining needs the pixels), and dropped when the user saves or
leaves the screen. "Estimate-then-discard" = discard when the review ends.

### Capture — native camera or photo library
Photos (and barcode photos) come in through a **single native file input**
(`<input type="file" accept="image/*">`) with **no `capture` attribute**, so the browser/OS
picks the source:
- **On mobile:** the OS offers a **Camera or Photo Library** chooser. Picking Camera uses the
  phone's own camera app — its shutter and use/retake — and hands the result back as the file
  (this is what lets you *snap* a barcode).
- **On desktop:** a normal file picker (choose a saved image).

This deliberately avoids an in-browser `getUserMedia` live-camera stream. That stream requires
a **secure context (HTTPS/localhost)** — which plain-HTTP LAN staging isn't — and offered no
way to pick an existing photo, so a single button couldn't both live-stream *and* let you
choose a file. The native input works everywhere with **no TLS dependency** and gives the OS
camera UI for free.

## Barcode fast-path (Phase 3)
Scanning an EAN/UPC off grocery packaging resolves to an **official** product
definition when one exists, else tells the user to describe or photograph it. It is a
**database lookup, not an AI estimate**, and lives in its own seam — full design in
**`barcode-lookup.md`**. (Uses the same native camera-or-upload capture as photo — see §Capture.)

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
  override AI values; the refine box appends a note and re-requests; the photo/barcode
  file input surfaces the chosen image; fallback message on error.
