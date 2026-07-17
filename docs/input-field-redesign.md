# Input-field redesign — meal logging + AI entry — implementation spec

> Status: **shipped in release 1.10.** This documents a UI redesign of the two food-logging
> input screens — the manual **Add Entry** form and the **Describe with AI** screen — plus
> a new Settings preference. No backend/API changes; this is frontend-only (React/Vite/TS)
> with localStorage for the one new persisted pref. Touches:
> [`EntryFormPage.tsx`](../frontend/src/pages/EntryFormPage.tsx),
> [`AiEntryPage.tsx`](../frontend/src/pages/AiEntryPage.tsx),
> [`SettingsPage.tsx`](../frontend/src/pages/SettingsPage.tsx),
> [`storage.ts`](../frontend/src/lib/storage.ts),
> [`entryform.css`](../frontend/src/styles/entryform.css),
> [`aientry.css`](../frontend/src/styles/aientry.css).

## Goal & summary

Tidy the two logging input surfaces and unify their confirm affordance:

1. **Manual Add Entry** — move the food **sort-order picker** out of the logging screen into
   **Settings** ("Sort foods by"); reword the "define new food" link; move the **AI** entry
   button to the **bottom**, **right-aligned**, relabelled **"Use AI Instead"**.
2. **Describe with AI** — split the input toggle into **three tabs** (Text / Photo / Barcode);
   introduce a reusable **checkmark confirm button** and apply it to per-action OK buttons and
   the Estimate/Re-estimate action; on Photo, collapse to a single **"Upload"** control while
   keeping the (resized) in-app camera; on Barcode, keep camera-or-upload + manual entry, with
   the lookup OK → checkmark.
3. **Settings** — new **"Sort foods by"** preference (local, like `groupedUnits`).

### Decisions taken (from the design Q&A + follow-up)

> **Superseded (post-1.10):** camera/upload was later simplified to a **single native file
> input** (no `capture` attribute) for both Photo and Barcode — mobile gets the OS "Camera or
> Photo Library" chooser and the native camera's own shutter/retake, with **no in-app
> `getUserMedia` live-camera stream** and therefore no secure-context/HTTPS requirement. The
> live-camera design described below (and the JSX snippets) is the original 1.10 shipment, since
> simplified — see `ai-estimation.md` §Capture for current behavior.
>
> **Superseded again (1.12):** the single input became **two explicit buttons** — "Take photo"
> (`capture="environment"`) and "File upload" (no `capture`) — and the whole AI input control
> (tabs, capture, estimate/refine) was extracted into the shared **`AiInputPanel`**, also hosted
> by the catalogue's **add-food** dialog, where results **auto-fill the food form** (the per-row
> Apply step is gone). Current behavior: `ai-estimation.md` §Capture and
> `food-catalogue-and-logging.md` §AI-assist panel.

- **One camera button per tab.** Photo and Barcode each expose a **single "Upload" control** —
  the empty state has no second button. On a **secure origin (HTTPS / localhost)** "Upload" opens
  the live camera (`getUserMedia`): this is what shows the **device-permission prompt on a
  computer** and the rear camera on a phone, and its in-app preview is sized to **42vh (≈70% of
  the old 60vh)** with a checkmark Capture. On a **non-secure origin** (e.g. plain-http LAN IP)
  `getUserMedia` is browser-blocked, so "Upload" falls back to the native file input (`capture`),
  which still surfaces the camera on phones. If the camera fails/denies on a secure origin, the
  control gracefully reveals the file input so the user is never stuck.
- **Secure-context caveat (important for testing):** a webcam permission prompt on a desktop is
  only possible from a secure origin. `http://<lan-ip>:3000` is **not** secure → no prompt (just
  the file picker). Use `http://localhost:3000` locally, or the HTTPS deploy URL.
- **Checkmark scope:** applies to the **per-action OK buttons** (camera Capture, barcode "Look
  up") **and** the **Estimate / Re-estimate** button. The final **"Save N items"** button
  **keeps its text** (it carries the item count).

---

## Part 1 — Manual Add Entry ([`EntryFormPage.tsx`](../frontend/src/pages/EntryFormPage.tsx))

### 1.1 Move the sort picker out of the screen

Today the sort `<select className="search-sort">` lives in a `.food-search-toolbar` directly
above the search box (`EntryFormPage.tsx:407-417`), driven by local `sortMode` state
(`:84`) and passed to the search query as `params.set('sort', sortMode)` (`:219`).

**Change:**

- **Delete** the `.food-search-toolbar` block (`:407-417`) and its CSS (`entryform.css`
  `.search-sort`, `.food-search-toolbar` — verify/remove).
- **Delete** the local `sortMode`/`setSortMode` state (`:84`).
- The sort value now comes from the **persisted preference** (see Part 3 / §3.1). Read it once at
  mount:
  ```ts
  const sortMode = getFoodSortMode(); // from ../lib/storage
  ```
  Keep it as the value passed to the search effect's `params.set('sort', sortMode)` (`:219`),
  and keep `sortMode` in that effect's dependency array (`:227`) — it's now a stable const per
  visit, so changing the Settings pref takes effect the next time the form is opened. (No live
  re-sort mid-form is required; the catalogue search re-runs on focus/typing anyway.)
- **Move** the `SortMode` type (`:24`) and `SORT_LABELS` map (`:26-31`) to `storage.ts` so both
  this page and Settings import one definition (renamed `FoodSortMode` / `FOOD_SORT_LABELS` —
  see §3.1). Remove the now-duplicate local copies here.

> Net effect: the search area becomes just the label + search input + the (reworded) link, with
> no inline sort control.

### 1.2 Reword the "define a new food" link

`EntryFormPage.tsx:454-456` currently renders:

```tsx
<button className="inline-define-link" onClick={() => setShowInlineForm(!showInlineForm)}>
  {showInlineForm ? 'Cancel' : "Can't find it? Define a new food"}
</button>
```

**Change** the non-toggle label to **`"Can't find it? Add it to the catalogue"`** (the
`'Cancel'` toggle label stays):

```tsx
{showInlineForm ? 'Cancel' : "Can't find it? Add it to the catalogue"}
```

### 1.3 Move the AI button to the bottom, right-aligned, "Use AI Instead"

Today the AI entry point is a full-width dashed button rendered at the **top** of the form, above
the search (`EntryFormPage.tsx:393-400`, class `.ai-describe-link`, label
`"✨ Describe it with AI instead"`).

**Change:**

- **Remove** the top block (`:393-400`).
- **Render it at the bottom** of the `!selectedFood` branch — after the inline-define link and
  the (conditional) `.inline-definition` form, still inside the `<>…</>` fragment that ends at
  `:490`. Keep the same guard `!isEdit && aiEnabled && !selectedFood`.
- **Right-align** it and **relabel** to **`"Use AI Instead"`**:
  ```tsx
  {!isEdit && aiEnabled && !selectedFood && (
    <div className="ai-entry-cta-row">
      <button
        className="ai-describe-link"
        onClick={() => navigate(`/entry/ai?meal=${encodeURIComponent(mealType)}&date=${queryDate}`)}
      >
        ✨ Use AI Instead
      </button>
    </div>
  )}
  ```
- **CSS** (`entryform.css`): add a right-aligning wrapper and make `.ai-describe-link` shrink to
  content instead of full-width block:
  ```css
  .ai-entry-cta-row {
    display: flex;
    justify-content: flex-end;
    margin-top: 1rem;
  }
  /* Inline variant for the bottom-right CTA: keep the dashed-indigo identity but size to
     content (the old full-width block style stays for any other use, or fold these in). */
  .ai-entry-cta-row .ai-describe-link {
    display: inline-block;
    width: auto;
    margin: 0;
    padding: 0.55rem 1.1rem;
  }
  ```
  (The existing `.ai-describe-link` hover — fill indigo, white text — is reused unchanged.)

> The label keeps the ✨ sparkle prefix: **`✨ Use AI Instead`** (carried over from the old
> "✨ Describe it with AI instead").

---

## Part 2 — Describe with AI ([`AiEntryPage.tsx`](../frontend/src/pages/AiEntryPage.tsx))

### 2.1 Three tabs: Text / Photo / Barcode

Today the mode is a 2-value union `'text' | 'photo'` (`:101`), the toggle renders two tabs
(`:531-548`) and only when `supportsText && (supportsImages || barcodeEnabled)`, and **Barcode
lives nested inside the Photo branch** (`:599-645`) under an "or" divider.

**Change — promote Barcode to a first-class tab:**

- Widen the union: `type AiMode = 'text' | 'photo' | 'barcode'`; `useState<AiMode>('text')`.
- **Default-mode selection** (replaces `:168-169`): pick the first enabled modality.
  ```ts
  setMode(sText ? 'text' : sImg ? 'photo' : bcOn ? 'barcode' : 'text');
  ```
- **Render a tab per enabled modality**, reusing the existing `.ai-mode-toggle` markup/styling
  (its buttons are `flex: 1`, so three tabs split evenly — no CSS change needed). Show the
  toggle whenever **two or more** modalities are enabled:
  ```tsx
  {[supportsText, supportsImages, barcodeEnabled].filter(Boolean).length > 1 && (
    <div className="ai-mode-toggle" role="tablist" aria-label="Input method">
      {supportsText && <button role="tab" type="button" aria-selected={mode==='text'}
        className={mode==='text'?'active':''} onClick={() => switchMode('text')} disabled={pending}>Text</button>}
      {supportsImages && <button role="tab" type="button" aria-selected={mode==='photo'}
        className={mode==='photo'?'active':''} onClick={() => switchMode('photo')} disabled={pending}>Photo</button>}
      {barcodeEnabled && <button role="tab" type="button" aria-selected={mode==='barcode'}
        className={mode==='barcode'?'active':''} onClick={() => switchMode('barcode')} disabled={pending}>Barcode</button>}
    </div>
  )}
  ```
  Note label change: the Text tab reads **"Text"** (was "Describe").
- **`switchMode`** (`:370-375`) → accept `AiMode`; stop the relevant cameras when leaving a tab:
  leaving anything that isn't `'photo'` stops the photo camera; leaving anything that isn't
  `'barcode'` stops the barcode scan. e.g.
  ```ts
  const switchMode = (m: AiMode) => {
    if (m === mode) return;
    if (m !== 'photo') stopCamera();
    if (m !== 'barcode') stopBarcodeScan();
    setMode(m); setError(null);
  };
  ```
- **Body branches** become three: `mode === 'text'` (`:550-565`, unchanged), `mode === 'photo'`
  (§2.3), `mode === 'barcode'` (§2.4). The barcode JSX **moves out** of the photo branch into its
  own branch; **delete** the `.ai-barcode-sep` "or" divider (`:601`) — tabs separate them now.
- **Estimate row** (`:655-661`): keep gating to text/photo (barcode has no estimate — it's
  lookup-driven). Condition becomes `mode === 'text' || (mode === 'photo' && supportsImages)`.
- **Refine** (`:740-755`): already gated `mode === 'text' || imageBlob` — still correct (a
  scanned barcode product has nothing to re-estimate). The refine label + textbox **stay**.

### 2.2 Reusable checkmark confirm button

Per "take the same style, do not copy paste", add one shared primitive and reuse it.

**New icon + component** (mirrors the existing inline-SVG pattern of `HomeIcon`/`EyeIcon`):

```tsx
// frontend/src/components/CheckButton.tsx
function CheckIcon() {
  return (
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <path d="M5 13l4 4L19 7" stroke="currentColor" strokeWidth="2.5"
            strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

export function CheckButton({ label, busy, ...props }:
  React.ButtonHTMLAttributes<HTMLButtonElement> & { label: string; busy?: boolean }) {
  return (
    <button type="button" className="check-btn" aria-label={label} title={label} {...props}>
      {busy ? <span className="ai-spinner" aria-hidden="true" /> : <CheckIcon />}
    </button>
  );
}
```

**CSS** (`aientry.css`) — square, indigo, reuses `.save-btn` palette but icon-only:

```css
.check-btn {
  flex: 0 0 auto;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 2.625rem;          /* matches .ai-photo-actions row height */
  height: 2.625rem;
  background: var(--primary);
  color: #fff;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.2s;
}
.check-btn:hover:not(:disabled) { opacity: 0.9; }
.check-btn:disabled { opacity: 0.6; cursor: not-allowed; }
```

Always pass a descriptive `label` (`aria-label`/tooltip) since the button has no visible text —
e.g. "Capture photo", "Look up barcode", "Estimate", "Re-estimate".

**Apply to:**

| Location | Was | Becomes |
|---|---|---|
| Photo in-app camera Capture (`:576`) | `<button className="save-btn">Capture</button>` | `<CheckButton label="Capture photo" onClick={capturePhoto} />` |
| Barcode manual "Look up" (`:636-638`) | `<button className="bc-btn">Look up</button>` | `<CheckButton label="Look up barcode" busy={barcodeBusy} onClick={() => lookupBarcode(barcodeCode)} disabled={barcodeBusy || !barcodeCode.trim()} />` |
| Estimate / Re-estimate (`:657-659`) | `<button className="save-btn ai-estimate-btn">{rows ? 'Re-estimate' : 'Estimate'}</button>` | `<CheckButton label={rows ? 'Re-estimate' : 'Estimate'} onClick={onEstimate} disabled={mode==='photo' && !imageBlob} />` |

**Not changed:** the final review **Save** button (`:772-774`) keeps `"Save N items"`; the
`Cancel` buttons keep text. (The barcode "Look up" busy state shows the spinner via `busy`,
replacing the old "Looking…" text.)

> The barcode Look-up checkmark sits in the manual-entry row (`.ai-barcode-manual`); since
> `.check-btn` is `flex: 0 0 auto` it pairs with the `flex: 1` input exactly like the old
> `.bc-btn`. The `.ai-estimate-row` (right-aligned, `:334-338`) already right-aligns its child,
> so the Estimate checkmark lands bottom-right as before — `.ai-estimate-btn` sizing is no longer
> needed (the checkmark is fixed-size); leave or remove that rule.

### 2.3 Photo tab — single "Upload", camera-first, resized

Original Photo branch had a two-button row `[Use camera] [Upload a file]`. **Collapse to one
button** — the "Upload" control, camera-first on a secure origin:

```tsx
{cameraOn ? (
  <div className="ai-camera">
    <video ref={videoRef} autoPlay playsInline muted className="ai-camera-preview" />
    <div className="ai-photo-actions">
      <CheckButton label="Capture photo" onClick={capturePhoto} />
      <button type="button" className="cancel-btn" onClick={stopCamera}>Cancel</button>
    </div>
  </div>
) : imageUrl ? (
  /* post-capture preview + Retake / remove — unchanged */
) : secureCamera() && !cameraFailed ? (
  /* secure origin → live camera (permission prompt on desktop, rear cam on phone) */
  <div className="ai-photo-actions">
    <button type="button" className="save-btn ai-upload-btn" onClick={startCamera}>Upload</button>
  </div>
) : (
  /* non-secure origin, or camera failed/denied → native file input (camera on phones via capture) */
  <div className="ai-photo-actions">
    <label className="save-btn ai-upload-btn">
      Upload
      <input type="file" accept="image/*" capture="environment" onChange={onFile} hidden aria-label="Upload a meal photo" />
    </label>
  </div>
)}
```

- The separate **"Use camera" button is gone**; "Upload" is the only control.
- **Graceful fallback:** add `const [cameraFailed, setCameraFailed] = useState(false)`; in
  `startCamera`'s `catch`, `setCameraFailed(true)` (+ a message) so a denied/missing camera on a
  secure origin drops back to the file input rather than dead-ending.
- **Capture** button → `CheckButton` (§2.2); **Cancel** stays.
- **Resize the camera preview to ≈70%**: the shared `.ai-camera-preview, .ai-photo-img` rule
  `max-height: 60vh` → **`42vh`** (`aientry.css`). Shared with the barcode camera — same
  overflow fix.
- **Post-capture preview**, **hint**, and the **Refine** block/textbox stay.

### 2.4 Barcode tab — own tab, OK → checkmark

The barcode JSX (`:599-645`) **moves** from inside Photo into its own `mode === 'barcode'`
branch. Behaviour is unchanged (camera scan via ZXing, upload-a-pic, manual digit entry → Open
Food Facts lookup → matched review row). Edits:

- **Drop** the `.ai-barcode-sep` "or" divider (`:601`) and the `{supportsImages && …}` guard
  around it — Barcode is now standalone, not "photo *or* barcode".
- **Single "Upload" control** (mirrors Photo §2.3): the old two-button row `[Use camera] [Upload
  a file]` collapses to one. Secure origin → live scan (`startBarcodeScan`, `.bc-btn`, "Upload");
  non-secure / camera-failed → file input (`onBarcodeFile`, `.bc-btn ai-upload-btn`, "Upload").
  Add `const [barcodeCameraFailed, setBarcodeCameraFailed] = useState(false)`; set it in
  `startBarcodeScan`'s `catch` **only when the stream never opened** (`!barcodeStreamRef.current`)
  so a mere "no barcode in frame" doesn't flip the fallback. Keep the lime accent.
- Keep the **in-app barcode camera** screen (`:604-611`); its preview uses the same
  `.ai-camera-preview` (now 42vh). It has only a Cancel (ZXing auto-decodes — no Capture/OK to
  convert).
- **Manual entry "Look up" → checkmark** (`:636-638`), via `CheckButton busy={barcodeBusy}`
  (§2.2). Keep the digit `<input>` and the `<details>"Or type the digits"` disclosure.
- Status/`ai-barcode-msg` lines stay.

> Availability edge case: if only Barcode is enabled (no text, no vision), the default-mode logic
> (§2.1) lands on `'barcode'` and the toggle is hidden (one modality). The `aiEnabled === false &&
> !barcodeEnabled` disabled-notice guard (`:522`) is unchanged.

---

## Part 3 — Settings ([`SettingsPage.tsx`](../frontend/src/pages/SettingsPage.tsx)) + storage

### 3.1 New persisted preference (`storage.ts`)

Add a local pref alongside `groupedUnits` (the sort order is a pure UI preference; no server
round-trip — same rationale as the grouped-unit picker). Also host the shared type/labels here so
`EntryFormPage` and `SettingsPage` share one definition:

```ts
// --- Food sort order (default order of the catalogue search while logging) ---
export type FoodSortMode = 'priority' | 'alphabetical' | 'most-used' | 'recent';
export const FOOD_SORT_LABELS: Record<FoodSortMode, string> = {
  priority: 'Priority',
  alphabetical: 'A–Z',
  'most-used': 'Most-used',
  recent: 'Recent',
};
export const getFoodSortMode = (): FoodSortMode =>
  read<FoodSortMode>('foodSortMode') ?? 'priority';
export const saveFoodSortMode = (mode: FoodSortMode) => write('foodSortMode', mode);
```

Default stays `'priority'` (matches today's hardcoded default at `EntryFormPage.tsx:84`).

### 3.2 "Sort foods by" control in Settings

Add a new **Preferences** `Section` (after Display), and style the control like the **Profile**
fields — a `SettingsField` (label above the input) in the same 2-col grid, **no help
description**:

```tsx
const [foodSortMode, setFoodSortModeState] = useState(getFoodSortMode()); // near :50

<Section title="Preferences">
  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem' }}>
    <SettingsField label="Sort foods by">
      <select
        value={foodSortMode}
        onChange={(e) => {
          const m = e.target.value as FoodSortMode;
          setFoodSortModeState(m);
          saveFoodSortMode(m);
        }}
      >
        {(Object.keys(FOOD_SORT_LABELS) as FoodSortMode[]).map((m) => (
          <option key={m} value={m}>{FOOD_SORT_LABELS[m]}</option>
        ))}
      </select>
    </SettingsField>
  </div>
</Section>
```

Imports to add: `getFoodSortMode, saveFoodSortMode, FoodSortMode, FOOD_SORT_LABELS` from
`../lib/storage`. Local-only — no `updatePrefs`/`updateProfile` call.

---

## Files & order of work

1. `frontend/src/lib/storage.ts` — add `FoodSortMode`/`FOOD_SORT_LABELS`/`getFoodSortMode`/
   `saveFoodSortMode` (§3.1).
2. `frontend/src/components/CheckButton.tsx` — new shared checkmark button (§2.2).
3. `frontend/src/pages/EntryFormPage.tsx` — remove sort picker, read pref; reword link; move AI
   button bottom-right (§1).
4. `frontend/src/pages/AiEntryPage.tsx` — 3 tabs, barcode own branch, CheckButton wiring, single
   Upload + camera resize wiring (§2).
5. `frontend/src/pages/SettingsPage.tsx` — "Sort foods by" select (§3.2).
6. `frontend/src/styles/entryform.css` — `.ai-entry-cta-row` + inline `.ai-describe-link`;
   remove `.search-sort`/`.food-search-toolbar` if now unused (§1).
7. `frontend/src/styles/aientry.css` — `.check-btn`; `.ai-camera-preview` max-height 60vh → 42vh
   (§2.2, §2.3).

## Verification checklist

Run the app (`project-startup` skill) and the suites
(`npm test --prefix frontend -- --run`, `dotnet test backend/Fuel.slnx -c Release`). Manually
verify in the browser preview:

- **Manual entry:** no sort dropdown on the screen; link reads "Can't find it? Add it to the
  catalogue"; "Use AI Instead" sits bottom-right and routes to `/entry/ai`.
- **Settings → Display:** "Sort foods by" select persists across reload; changing it reorders the
  next Add-Entry catalogue search.
- **AI screen:** three tabs (Text / Photo / Barcode) when all modalities are on; correct subset
  when some are off; default tab = first enabled.
- **Checkmarks:** camera Capture, barcode "Look up", and Estimate/Re-estimate render as a
  checkmark icon with the right `aria-label`; "Save N items" still shows text.
- **Photo:** single "Upload" control; in-app camera preview is visibly shorter (42vh) and the
  checkmark/Cancel row is on-screen without scrolling; upload + estimate + refine still work.
- **Barcode:** own tab (no "or" divider); camera scan, upload, and manual lookup all add a
  matched review row; lookup checkmark shows a spinner while busy.
- **a11y:** tab `role="tab"`/`aria-selected` intact; every icon-only button has an `aria-label`.
- **Regression:** save (single + batch), refine, undo-edits, and the meal/time pickers unchanged.
