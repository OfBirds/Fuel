# AI providers — deploy-time, swappable (design)

> Status: **spec, not yet built** (Phase 2+). The AI provider that estimates
> calories/macros from text and photos — and later generates food icons — is
> chosen by the **operator at deploy time**, never a user setting. It must be
> **easily swappable**. **Starting provider: DeepSeek.**

## Principle
- **One seam:** `INutritionEstimator`. The rest of the app depends only on it.
- **Selected at startup** from flat `AI_*` env vars (same style as `SMTP_*` /
  `DB_*`), read in `Program.cs`.
- **Adding a provider = a new class + a factory case.** No caller changes.

## Configuration (flat env vars)
| Key | Purpose |
|---|---|
| `AI_PROVIDER` | which implementation (e.g. `deepseek`) |
| `AI_API_KEY` | **secret** — only in `/opt/fuel/.env.*`, never committed |
| `AI_BASE_URL` | provider endpoint |
| `AI_MODEL` | model id |
| `AI_ENABLED` | off → app runs manual-only |
| `AI_TIMEOUT_SECONDS` | guard for the slow external call |

Wire in `Program.cs`; pass through the `app` service in `deploy/docker-compose.yml`;
add the **non-secret** keys to `deploy/.env.*.example`. The key itself is handled
like `SMTP_PASS` (see `docs/deploy-runbook.md` and the `setup-deploy-pipeline`
skill).

## The interface
```
INutritionEstimator
  bool SupportsImages                                  // capability flag
  Task<NutritionEstimate> EstimateFromTextAsync(string description, string? hint)
  Task<NutritionEstimate> EstimateFromImageAsync(byte[] image, string contentType, string? hint)

NutritionEstimate { Name, Items[] {Name, Quantity, Uom}, Quantity, Uom,
                    Calories, Protein, Carbs, Fat, Confidence }
```
- Output is **structured JSON**, never free text — the model is asked for a
  schema-shaped result (DeepSeek JSON mode; Claude structured outputs).
- The estimate **pre-fills the unified entry screen**; it is never written
  straight to history. If the food is new, it is **defined into the catalogue
  first** (see `food-catalogue-and-logging.md`).
- `SupportsImages` lets the photo path fall back cleanly for any future text-only
  provider.

## Providers
- **`DeepSeekEstimator` (first)** — text **and** image (DeepSeek supports image
  input). Base URL `https://api.deepseek.com`; confirm the multimodal model id for
  `AI_MODEL`.
- **Registry/factory** keyed by `AI_PROVIDER` → DI registration in `Program.cs`.
- **Future — a Claude vision estimator** (official Anthropic C# SDK): vision-capable
  models `claude-haiku-4-5` ($1/$5 per MTok), `claude-sonnet-4-6` ($3/$15),
  `claude-opus-4-8` ($5/$25); use structured outputs for the JSON result.

## Resilience
Time-box every call (`AI_TIMEOUT_SECONDS`). On failure/timeout, return a clear
"couldn't estimate — enter it manually" and let the manual path proceed; never
block logging. Log via Serilog (→ Seq), tagged with provider + model.

## Icons (Phase 3)
The same provider abstraction can generate a per-food icon at definition time
(toggleable off in Settings). Generated icons live on a Docker volume (like
backups), not the DB.
