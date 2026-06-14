# AI providers — deploy-time, swappable (design)

> Status: **built** (Phase 2 text + Phase 3 photo). The AI provider that estimates
> calories/macros from text and photos — and later generates food icons — is
> chosen by the **operator at deploy time**, never a user setting, and is
> **easily swappable**. **Text: DeepSeek. Photo: Claude (Haiku).**

## Principle
- **One seam:** `INutritionEstimator`. The rest of the app depends only on it.
- **Selected at startup** from flat `AI_*` env vars (same style as `SMTP_*` /
  `DB_*`), read in `Program.cs`.
- **Both shipping providers speak the Anthropic Messages API**, so a single
  implementation — `AnthropicEstimator` — serves both. They differ *only by
  connection*: endpoint + key + model (`AnthropicConnection`). DeepSeek exposes an
  Anthropic-compatible endpoint at `api.deepseek.com/anthropic`; Claude uses its own.
- **A composite** routes text→DeepSeek, photo→Claude (photo needs Claude because
  DeepSeek's v4 models are text-only — see below). If only one connection is
  configured it serves both directions.
- **Adding a same-format provider = a new connection** (env values), no code.
  **Adding an OpenAI-format provider** = wire the parked `OpenAiEstimator`
  (chat/completions) — kept ready for OpenAI / Azure OpenAI / vLLM / etc.

## Configuration (flat env vars)
| Key | Purpose |
|---|---|
| `AI_PROVIDER` | informational label for the text connection (e.g. `deepseek`) |
| `AI_API_KEY` | **secret** — text connection key; only in `/opt/fuel/.env.*` |
| `AI_BASE_URL` | text endpoint — DeepSeek's Anthropic-compatible `…/anthropic` |
| `AI_MODEL` | text model id (e.g. `deepseek-v4-flash`) |
| `AI_ENABLED` | off → app runs manual-only |
| `AI_TIMEOUT_SECONDS` | guard for the slow external call |
| `AI_CLAUDE_ENABLED` | enable the photo (vision) connection |
| `AI_CLAUDE_API_KEY` | **secret** — Anthropic key for the photo connection |
| `AI_CLAUDE_BASE_URL` | photo endpoint (default `https://api.anthropic.com`) |
| `AI_CLAUDE_MODEL` | vision model id (e.g. `claude-haiku-4-5-20251001`) |

### Why photo ≠ DeepSeek
DeepSeek's `/anthropic` endpoint *accepts* an image block (HTTP 200) but the v4
models are text-only: the API substitutes the image with `[Unsupported Image]`
before the model sees it, so it returns nothing usable (or hallucinates a generic
plate). Verified directly against `deepseek-v4-flash`/`-pro`. Hence photos route to
Claude. The day DeepSeek ships a vision model, flip `SupportsImages` on its
connection — no other change.

Wire in `Program.cs`; pass through the `app` service in `deploy/docker-compose.yml`;
add the **non-secret** keys to `deploy/.env.*.example`. The key itself is handled
like `SMTP_PASS` (see `docs/deploy-runbook.md` and the `setup-deploy-pipeline`
skill).

## The interface
```
INutritionEstimator
  bool SupportsImages                                  // capability flag
  Task<NutritionEstimate> EstimateFromTextAsync(string description, IReadOnlyList<string> notes)
  Task<NutritionEstimate> EstimateFromImageAsync(byte[] image, string contentType, IReadOnlyList<string> notes)

NutritionEstimate {
  Items[] {                       // ONE query → MANY foods (see ai-estimation.md)
    Name, Quantity, Uom,
    Calories, Protein, Carbs, Fat,
    Confidence,                   // per-item 0..1
    MatchedFoodId?                // set when resolved to an existing catalogue Food
  },
  OverallConfidence
}
```
- **One query → many items.** A single description ("chicken 1 kg with 100 g
  salad") or photo yields a **list** of line-items, each editable, each becoming
  its own `FoodEntry`. Word order is the model's problem, not ours.
- **`notes` drives the refine loop.** Empty on the first call. Each follow-up
  clarification the user adds (see `ai-estimation.md` §refine) is appended and the
  call is re-issued. **The server stays stateless** — the client owns the thread
  (and, for photos, holds the image in memory across turns).
- Output is **structured JSON**, never free text — the model is asked for a
  schema-shaped result (DeepSeek JSON mode; Claude structured outputs). Malformed
  or schema-invalid output is treated as a failure (→ manual fallback), never
  trusted.
- The estimate **pre-fills the unified entry screen**; it is never written
  straight to history. If an item's food is new, it is **defined into the catalogue
  first** (see `food-catalogue-and-logging.md`) and badged "new — added".
- `SupportsImages` lets the photo path fall back cleanly for any future text-only
  provider. **Barcode lookup is a separate seam** (`IBarcodeFoodLookup`, see
  `barcode-lookup.md`) — it is a database lookup, not an AI estimate.

## Providers
- **`AnthropicEstimator` (built)** — the single implementation behind both shipping
  connections, over the Anthropic Messages API (`v1/messages`). One quirk it handles:
  DeepSeek's endpoint is a *reasoner* and prepends a `thinking` content block before
  the answer, so the parser takes the first **text** block, not the first block.
- **Text connection → DeepSeek, `deepseek-v4-flash`** via its Anthropic-compatible
  `/anthropic` endpoint. Model chosen after benchmarking v4-flash, v4-pro, v3-chat,
  Claude Haiku/Sonnet/Opus on multilingual food descriptions (SR/DE/RU): v4-flash
  matched or beat v4-pro on Latin-script accuracy at far lower cost (~$0.14/M in,
  $0.28/M out). (Via the `/anthropic` endpoint the reasoning step adds ~3–4s latency
  vs. the OpenAI endpoint — still well inside the timeout.)
- **Photo connection → Claude, `claude-haiku-4-5`** — required, because DeepSeek's v4
  models are text-only (see §"Why photo ≠ DeepSeek"). Haiku is competitive on accuracy
  and the cheapest vision-capable Claude.
- **`OpenAiEstimator` (parked)** — generic OpenAI-compatible `/chat/completions` impl,
  not wired; kept ready for a future OpenAI-format provider.
- **Wiring** lives in `Program.cs`: two named `HttpClient`s (`ai-text`, `ai-image`)
  with resilience pipelines, two `AnthropicEstimator`s, one composite.

## Resilience
The estimator is a slow, fallible **external** call — treat every call as
best-effort:
- **Time-box** every call (`AI_TIMEOUT_SECONDS`); **one retry** on transient
  network / 5xx errors.
- **On failure/timeout** return a clear "couldn't estimate — enter it manually"
  and let the manual path proceed; **never block logging**.
- **Validate the shape** — malformed/non-conforming JSON counts as a failure.
- When `AI_ENABLED=false` (or after repeated failures), the AI affordances render
  **disabled with a tooltip**, not broken.
- **Log** via Serilog (→ Seq), tagged with provider + model + latency +
  resulting confidence. **Photos are never logged or persisted** (see
  `ai-estimation.md` for the in-memory lifetime).

## Icons (Phase 3)
The same provider abstraction can generate a per-food icon at definition time
(toggleable off in Settings). Generated icons live on a Docker volume (like
backups), not the DB.
