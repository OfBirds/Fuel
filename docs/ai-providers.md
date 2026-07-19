# AI providers — deploy-time, swappable (design)

> Status: **built** (Phase 2 text + Phase 3 photo). Estimation runs through an **ordered
> registry of providers** — N connectors, tried in priority order per modality with
> automatic fallback. Local-first + cloud-fallback is the headline use case (home-lab
> self-host with a public safety net). Default cloud set: **text → DeepSeek, photo →
> Claude (Haiku)**, but operators reorder/add/disable providers **live, no redeploy**.

## Principle
- **One seam:** `INutritionEstimator` (one estimator = one provider connection). Two
  impls, one per wire convention: `AnthropicEstimator` (Messages API) and
  `OpenAiEstimator` (chat/completions — also self-hosted **Ollama**/vLLM/etc.).
- **A provider is pure data:** `{ name, convention, capabilities, baseUrl, model, order,
  enabled, keyRef }`. The whole registry is a list of these.
- **`EstimatorChain` dispatches.** For the requested modality (text/vision) it takes
  every enabled provider with that capability, sorts by `order` (lower first), and tries
  them in turn — **first success wins; any failure falls through to the next** (timeout /
  network / 5xx / bad JSON / empty result, incl. DeepSeek's `[Unsupported Image]` no-op).
  A user cancel stops the chain. All fail → manual fallback. No provider for a modality →
  that feature is blocked with "not configured".
- **Config split — list vs. secrets:**
  - The **provider list** lives in a **hot-reloadable JSON file** (`AI_CONFIG_FILE`,
    bind-mounted; `reloadOnChange` via `IOptionsMonitor`). Edit it → reorder, enable/
    disable, swap a model, change a URL, or add a provider that reuses an existing key —
    **all live, no redeploy.**
  - **Secret key VALUES** stay in flat `AI_KEY_<NAME>` env vars. A provider names one via
    `keyRef` (`keyRef: "claude"` → `AI_KEY_CLAUDE`); omit it for key-less local servers.
    Adding a **new secret** is the only change that needs a redeploy.

## Configuration
**Env (secrets + wiring, redeploy to change):**
| Key | Purpose |
|---|---|
| `AI_CONFIG_FILE` | path to the providers JSON inside the app (compose sets `/app/ai-providers.json`) |
| `AI_CONFIG_HOST_PATH` | host path bind-mounted to the above (e.g. `/opt/fuel/ai-providers.staging.json`) |
| `AI_KEY_<NAME>` | **secret** key value, referenced by a provider's `keyRef` (e.g. `AI_KEY_CLAUDE`) |
| `AI_TIMEOUT_SECONDS` | per-call time-box for the slow external call |

**Providers JSON (the registry, hot-reloaded)** — see `deploy/ai-providers.example.json`:
```json
{ "ai": { "providers": [
  { "name": "qwen-local", "convention": "openai", "capabilities": ["text","vision"],
    "baseUrl": "http://ollama-host:11434/v1", "model": "qwen2.5-vl", "order": 1 },
  { "name": "deepseek", "convention": "anthropic", "capabilities": ["text"],
    "baseUrl": "https://api.deepseek.com/anthropic", "model": "deepseek-v4-flash",
    "order": 2, "keyRef": "deepseek" },
  { "name": "claude", "convention": "anthropic", "capabilities": ["vision","text"],
    "baseUrl": "https://api.anthropic.com", "model": "claude-haiku-4-5-20251001",
    "order": 2, "keyRef": "claude" }
] } }
```
This yields **local-first, cloud-fallback for both modalities**: text = qwen(1) → deepseek(2);
vision = qwen(1) → claude(2). `convention` picks the wire format; `capabilities` picks the
chain(s); `order` picks priority; `enabled:false` parks an entry; `keyRef` (optional) maps to
an env key.

> **Note on config style:** this is the one place we use a JSON config file rather than the
> flat-env-var convention in `CLAUDE.md` — the list is structured (N×fields) and must
> hot-reload, which env vars can't. Secrets still follow the flat-env rule (`AI_KEY_*`).

### Why default photo ≠ DeepSeek
DeepSeek's `/anthropic` endpoint *accepts* an image block (HTTP 200) but the v4 models are
text-only: the API substitutes the image with `[Unsupported Image]` before the model sees
it, so it returns nothing usable (or hallucinates a generic plate). Verified against
`deepseek-v4-flash`/`-pro`. So we don't give DeepSeek the `vision` capability — and if a
vision model ever ships, just add `"vision"` to its capabilities. (The chain treats an empty
result as a failure, so even a mis-tagged text-only model degrades gracefully to the next.)

## The interface
```
INutritionEstimator                                    // one estimator = one connection
  Task<NutritionEstimate> EstimateFromTextAsync(string description, IReadOnlyList<string> notes)
  Task<NutritionEstimate> EstimateFromImageAsync(byte[] image, string contentType, IReadOnlyList<string> notes)

EstimatorChain (what the controller depends on)        // capability gating + ordered fallback
  bool SupportsText / bool SupportsImages              // does any enabled provider cover it?
  EstimateFromTextAsync / EstimateFromImageAsync       // walk the sorted chain, first success wins

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
- **Barcode lookup is a separate seam** (`IBarcodeFoodLookup`, see `barcode-lookup.md`) —
  it is a database lookup, not an AI estimate.

## Providers
- **`AnthropicEstimator`** — Anthropic Messages API (`v1/messages`). Drives Claude and
  DeepSeek (via its `/anthropic` endpoint). Handles a reasoner quirk: DeepSeek prepends a
  `thinking` content block, so the parser takes the first **text** block, not the first.
- **`OpenAiEstimator`** — OpenAI-compatible `/chat/completions` (JSON mode), incl. the
  `image_url` vision part. Drives OpenAI/Azure and **self-hosted Ollama/vLLM/SGLang**;
  Bearer header is omitted when a connection has no key.
- **`EstimatorChain`** — the dispatcher the controller depends on. Reads the hot-reloaded
  registry, builds the per-modality ordered chain, constructs a connector per provider
  (resolving `keyRef` → `AI_KEY_*` at request time), and runs the fallback loop.
- **Model notes (default cloud set):** text `deepseek-v4-flash` — chosen after benchmarking
  v4-flash/v4-pro/v3-chat and Claude Haiku/Sonnet/Opus on multilingual food descriptions
  (SR/DE/RU); matched or beat v4-pro on Latin-script accuracy at far lower cost (~$0.14/M
  in, $0.28/M out). The `/anthropic` reasoning step adds ~3–4s vs. the OpenAI endpoint —
  inside the timeout. Photo `claude-haiku-4-5` — cheapest vision-capable Claude. For
  self-hosting alternatives + hardware, a Qwen2.5-VL / Gemma-3 / Llama-Vision class model
  on a 24GB+ GPU via Ollama covers both modalities through `OpenAiEstimator`.
- **Wiring** (`Program.cs`): one shared named `HttpClient` `"ai"` with the resilience
  pipeline (timeout + one retry); connectors send absolute URIs built from each provider's
  `baseUrl`, so a single client serves every provider. `EstimatorChain` is the singleton.

## Resilience
The estimator is a slow, fallible **external** call — treat every call as
best-effort:
- **Time-box** every call (`AI_TIMEOUT_SECONDS`); **one retry** on transient
  network / 5xx / 408 errors — **429 is excluded from this retry** (see below).
- **Rate limits (429) are a distinct failure mode**, not a generic transient error:
  - Both estimators recognize a 429 response and throw `AiRateLimitedException`
    instead of the generic `AiUnavailableException`, carrying the response's
    `Retry-After` when the provider sends one.
  - The HTTP-level retry (`Program.cs`, the `"ai"` client's resilience pipeline)
    deliberately does **not** retry a 429 — retrying an already rate-limited call
    just burns the timeout budget on a request that will fail again.
  - `EstimatorChain` catches `AiRateLimitedException` and puts **that provider**
    into a cooldown (until `Retry-After` elapses, or 60s if the provider didn't
    send one) before falling through to the next provider in the chain. Later
    calls **skip a cooling-down provider outright** — no wasted HTTP round-trip —
    until the cooldown expires. The cooldown lives on the `EstimatorChain`
    singleton, so it's shared across all requests, not just the one that hit
    the limit.
  - Practical effect: if the default vision provider (Claude) gets rate-limited,
    every subsequent photo estimate falls straight through to the next enabled
    vision provider (if any) instead of re-hitting Claude and eating the timeout
    each time. **Vision has only one cloud provider by default** — see
    `deploy/ai-providers.example.json`'s disabled `openai-gpt4o-mini` entry for
    a ready-to-enable fallback (set `AI_KEY_OPENAI`, flip `enabled: true`, no
    redeploy needed for the rest).
- **On failure/timeout** return a clear "couldn't estimate — enter it manually"
  and let the manual path proceed; **never block logging**.
- **Validate the shape** — malformed/non-conforming JSON counts as a failure.
- When no provider covers a modality (empty/absent registry, or every provider failed),
  that affordance renders **disabled / "not configured"**, not broken — text and photo
  are gated independently via `/api/ai/status` (`supportsText`, `supportsImages`).
- **Log** via Serilog (→ Seq), tagged with provider + model + latency +
  resulting confidence. **Photos are never logged or persisted** (see
  `ai-estimation.md` for the in-memory lifetime).

## Icons (Phase 3)
The same provider abstraction can generate a per-food icon at definition time
(toggleable off in Settings). Generated icons live on a Docker volume (like
backups), not the DB.
