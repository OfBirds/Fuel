# AI calorie estimation — text & photo (Phase 2 & 3) — design

> Status: **spec, not yet built**. Phase 2 = estimate from a **typed description**;
> Phase 3 = estimate from a **photo**. Both reuse the unified entry screen from
> Phase 0 and the provider abstraction in `docs/ai-providers.md`.

## Goal
Let the user log food without doing the lookup: **type** a description ("gurmanska
pljeskavica 300 gr in a bun") or **snap a photo**, and the configured AI provider
returns a structured `{ name, items, quantity, uom, calories, protein, carbs, fat,
confidence }`. The result **pre-fills the same editable entry screen** the manual
path uses — the user always confirms or edits before it's saved.

## Phase 2 — text
1. On the entry screen, the user types a description and hits "estimate".
2. Backend calls `INutritionEstimator.EstimateFromTextAsync` (provider chosen by
   `AI_PROVIDER`; DeepSeek first).
3. The structured estimate pre-fills the form (food name, quantity, UoM,
   calories/macros, confidence). The user edits/deletes anything — suggested
   ingredients, calories, weight, UoM — exactly like manual.
4. **New food → defined into the catalogue first** (see
   `food-catalogue-and-logging.md`), then the `FoodEntry` references it.
   `Source = AiText`, `AiConfidence` recorded.
5. **Fallback:** on timeout/failure, surface "couldn't estimate — enter it
   manually" and leave the manual path fully usable.

## Phase 3 — photo
1. The PWA captures or uploads a photo (camera on mobile).
2. Backend hands the bytes to `EstimateFromImageAsync` (DeepSeek supports image
   input). **The photo is not stored** — estimate-then-discard.
3. Same flow as text from step 3 on; `Source = AiPhoto`.
4. If the photo result is wrong, the user can re-type for AI or fall back to
   manual.

## Catalogue lookup (Phase 0 simple; optimization later)
When the AI names a food, look it up in the catalogue: exact/explicit match →
reuse; otherwise **create** it. The **fuzzy-match / dedup / "did you mean"**
optimization is a **later phase**, not now.

## Cost & resilience
- Calls are external and slow → time-boxed (`AI_TIMEOUT_SECONDS`); never block the
  manual loop.
- Log each call via Serilog (→ Seq), tagged with provider/model and the resulting
  confidence. Photos are never logged or persisted.

## Out of scope here
Provider wiring/config and the interface live in `docs/ai-providers.md`. Per-food
AI icons are Phase 3 but specified in `ai-providers.md`.

## Tests
- **Backend:** with a **fake `INutritionEstimator`**, the controller maps an
  estimate onto a prefilled entry; new-food path creates a catalogue `Food` then
  the entry; timeout/failure yields the manual-fallback response; `Source` and
  `AiConfidence` set correctly. (No live provider calls in tests.)
- **Frontend:** "estimate" populates the form; user edits override AI values;
  fallback message shows on error.
