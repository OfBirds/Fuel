import { useState, useRef, useCallback, useEffect, useMemo } from 'react';
import { apiFetch } from '../lib/api';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getLastMealType, saveLastMealType } from '../lib/storage';
import { normalizeImage } from '../lib/image';
import { loadCatalogueByName, type CatalogueFood } from '../lib/foods';
import { useShowMacros } from '../hooks/useShowMacros';
import { useAiStatus } from '../hooks/useAiStatus';
import { convertToSystem, inferPreferredSystem } from '../lib/units';
import { UnitSelect } from '../components/UnitSelect';
import { NumberInput } from '../components/NumberInput';
import { CheckButton } from '../components/CheckButton';
import { PhotoPickButton } from '../components/PhotoPickButton';
import '../styles/entryform.css';
import '../styles/aientry.css';

const MEAL_OPTIONS = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];
const LOW_CONFIDENCE = 0.5;

type AiMode = 'text' | 'photo' | 'barcode';

interface ApiRow {
  name: string;
  quantity: number;
  uom: string;
  calories: number;
  protein: number | null;
  carbs: number | null;
  fat: number | null;
  confidence: number;
  matchedFoodId: string | null;
  matchedDefaultUoM: string | null;
  isNew: boolean;
}

interface EstimateApiResponse {
  ok: boolean;
  error: string | null;
  overallConfidence: number;
  source: string;
  items: ApiRow[];
}

interface Row extends ApiRow {
  key: string;
  // Per-unit ratios so a quantity change rescales calories/macros. Derived from whatever
  // absolute values the row currently holds (AI estimate, matched food, or a manual edit).
  caloriesPerUnit: number;
  proteinPerUnit: number | null;
  carbsPerUnit: number | null;
  fatPerUnit: number | null;
}

const perUnit = (total: number, qty: number) => (qty > 0 ? total / qty : 0);
const perUnitOpt = (total: number | null, qty: number) =>
  total == null ? null : qty > 0 ? total / qty : 0;

// Attach per-unit ratios to a raw API row (AI estimate), derived from its own quantity.
const withRatios = (r: ApiRow): Omit<Row, 'key'> => ({
  ...r,
  caloriesPerUnit: perUnit(r.calories, r.quantity),
  proteinPerUnit: perUnitOpt(r.protein, r.quantity),
  carbsPerUnit: perUnitOpt(r.carbs, r.quantity),
  fatPerUnit: perUnitOpt(r.fat, r.quantity),
});

// Shape of the catalogue food returned by /api/barcode/lookup (FoodResponse).
interface BarcodeFood {
  id: string;
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
  proteinPerUnit: number | null;
  carbsPerUnit: number | null;
  fatPerUnit: number | null;
}

const round1 = (n: number) => Math.round(n * 10) / 10;

// Mirrors EstimateController.NormalizeName (backend) so the frontend's duplicate check
// matches how the backend actually resolves a name to an existing food: lowercase, then
// strip anything from the first "(" onward (parenthetical qualifiers), then trim.
const normalizeFoodName = (raw: string): string => {
  const lower = raw.toLowerCase();
  const paren = lower.indexOf('(');
  return (paren >= 0 ? lower.slice(0, paren) : lower).trim();
};

let keySeq = 0;
const nextKey = () => `row-${keySeq++}`;

function localDate(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function nowTime(): string {
  const d = new Date();
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

function AiEntryPage() {
  const { user } = useAuth();
  const showMacros = useShowMacros();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryMeal = searchParams.get('meal') || getLastMealType();
  const queryDate = searchParams.get('date') || localDate();

  const { aiEnabled, supportsText, supportsImages } = useAiStatus();
  const [mode, setMode] = useState<AiMode>('text');
  const [description, setDescription] = useState('');
  const [rows, setRows] = useState<Row[] | null>(null);
  // Snapshot of the AI's suggestion as returned, so manual edits can be undone back to it
  // without re-running the estimate.
  const [aiSnapshot, setAiSnapshot] = useState<Row[] | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [noteDraft, setNoteDraft] = useState('');
  const [overallConfidence, setOverallConfidence] = useState<number | null>(null);
  const [source, setSource] = useState('AiText');

  // Photo capture: the blob lives in browser memory for this review only and is
  // re-sent on each refine turn — never persisted (docs/ai-estimation.md §Image lifetime).
  // Photos arrive through a native file input with no `capture` attribute, so phones offer
  // the OS "Camera or Photo Library" chooser and the native camera's own shutter + retake.
  const [imageBlob, setImageBlob] = useState<Blob | null>(null);
  const [imageUrl, setImageUrl] = useState<string | null>(null);
  // Optional free-text hint the user attaches to the photo — rides with the FIRST estimate
  // (and every re-send) so the model weighs it alongside what it sees. Refine notes are separate.
  const [photoNote, setPhotoNote] = useState('');

  // Barcode (EAN/UPC) scan — its own tab. A precise digit decode (ZXing) run locally on the
  // captured/chosen photo + Open Food Facts lookup, not an AI guess; a hit becomes a matched row.
  const [barcodeEnabled, setBarcodeEnabled] = useState(false);
  const [barcodeStatusKnown, setBarcodeStatusKnown] = useState(false);
  const modeInitialized = useRef(false);
  const [barcodeCode, setBarcodeCode] = useState('');
  const [barcodeBusy, setBarcodeBusy] = useState(false);
  const [barcodeMsg, setBarcodeMsg] = useState<string | null>(null);

  // Catalogue keyed by lowercased name — to flag a "new" row that actually duplicates
  // an existing food (we don't auto-merge yet; just warn so we stop minting dupes).
  const [catalogue, setCatalogue] = useState<Map<string, CatalogueFood>>(new Map());
  // Inferred from the user's own catalogue usage (§3b-R6) — recomputed per catalogue load,
  // never persisted. Drives unit-system normalization of AI/estimate rows below.
  const preferredSystem = useMemo(
    () => inferPreferredSystem(Array.from(catalogue.values())),
    [catalogue],
  );

  const [mealType, setMealType] = useState(queryMeal);
  const [intakeAt, setIntakeAt] = useState(() => `${queryDate}T${nowTime()}`);

  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  // Barcode capability stays local (useAiStatus only covers /api/ai/status).
  useEffect(() => {
    let alive = true;
    (async () => {
      let bcOn = false;
      try {
        const res = await apiFetch('/api/barcode/status');
        if (res.ok) bcOn = (await res.json()).enabled === true;
      } catch { /* leave barcode off */ }
      if (!alive) return;
      setBarcodeEnabled(bcOn);
      setBarcodeStatusKnown(true);
    })();
    return () => { alive = false; };
  }, []);

  // Start on the first enabled modality (text → photo → barcode) once both capability
  // fetches (AI + barcode) have resolved — picked once, not re-run on later state changes.
  useEffect(() => {
    if (aiEnabled === null || !barcodeStatusKnown || modeInitialized.current) return;
    modeInitialized.current = true;
    setMode(supportsText ? 'text' : supportsImages ? 'photo' : barcodeEnabled ? 'barcode' : 'text');
  }, [aiEnabled, barcodeStatusKnown, supportsText, supportsImages, barcodeEnabled]);

  // Load the catalogue once for duplicate-name detection on "new" review rows, and to
  // infer the user's preferred unit system (usageCount per food) for §3b-R6 normalization.
  useEffect(() => {
    if (!user) return;
    let alive = true;
    loadCatalogueByName(user.id).then((m) => { if (alive) setCatalogue(m); });
    return () => { alive = false; };
  }, [user?.id]);

  // Abort any in-flight request if we leave the screen.
  useEffect(() => () => { abortRef.current?.abort(); }, []);

  // Revoke the previous object URL when the photo changes or the screen unmounts.
  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl); }, [imageUrl]);

  const useImage = async (blob: Blob) => {
    setRows(null); setNotes([]); setOverallConfidence(null); setError(null);
    // Re-encode to JPEG + downscale before it ever leaves the browser: HEIC and
    // multi-MB phone photos otherwise 400 at the vision provider (see lib/image).
    let normalized = blob;
    try { normalized = await normalizeImage(blob); } catch { /* fall back to original */ }
    setImageBlob(normalized);
    setImageUrl(URL.createObjectURL(normalized)); // prior URL revoked by the effect above
  };

  const clearImage = () => { setImageBlob(null); setImageUrl(null); };

  // ── Barcode scan (EAN/UPC) — decode locally from a captured/chosen photo, then look up ──
  // A resolved product is already a real catalogue food (the backend caches it),
  // so it joins the review list as a matched row — not a "new" one.
  const addBarcodeFood = (food: BarcodeFood) => {
    const qty = food.defaultUoM === 'piece' ? 1 : 100;
    const scaled = (v: number | null) => (v == null ? null : round1(v * qty));
    setRows((rs) => [
      ...(rs ?? []),
      {
        key: nextKey(),
        name: food.name,
        quantity: qty,
        uom: food.defaultUoM,
        calories: Math.round(food.caloriesPerUnit * qty),
        protein: scaled(food.proteinPerUnit),
        carbs: scaled(food.carbsPerUnit),
        fat: scaled(food.fatPerUnit),
        caloriesPerUnit: food.caloriesPerUnit,
        proteinPerUnit: food.proteinPerUnit,
        carbsPerUnit: food.carbsPerUnit,
        fatPerUnit: food.fatPerUnit,
        confidence: 1,
        matchedFoodId: food.id,
        matchedDefaultUoM: food.defaultUoM,
        isNew: false,
      },
    ]);
    setSource('Barcode');
  };

  const lookupBarcode = useCallback(async (code: string) => {
    const clean = code.replace(/[^0-9]/g, '');
    if (!clean) return;
    setBarcodeBusy(true);
    setBarcodeMsg(null);
    try {
      const res = await apiFetch(`/api/barcode/lookup/${encodeURIComponent(clean)}`);
      const data = await res.json();
      if (res.ok && data.found && data.food) {
        addBarcodeFood(data.food as BarcodeFood);
        setBarcodeCode('');
      } else {
        setBarcodeMsg(data.message || 'Product not found — describe it or enter it manually.');
      }
    } catch {
      setBarcodeMsg('Lookup failed — try again, or enter it manually.');
    } finally {
      setBarcodeBusy(false);
    }
  }, []);

  // Decode a barcode straight from a chosen/snapped photo (PhotoPickButton's camera/library
  // action), then look it up.
  const onBarcodeFile = async (file: File) => {
    setBarcodeMsg(null);
    setBarcodeBusy(true);
    const url = URL.createObjectURL(file);
    try {
      const { BrowserMultiFormatReader } = await import('@zxing/browser');
      const reader = new BrowserMultiFormatReader();
      const result = await reader.decodeFromImageUrl(url);
      if (result) await lookupBarcode(result.getText());
      else setBarcodeMsg('No barcode found in that image — try a clearer photo or type the digits.');
    } catch {
      setBarcodeMsg("Couldn't read a barcode in that image — try a clearer photo or type the digits.");
    } finally {
      URL.revokeObjectURL(url);
      setBarcodeBusy(false);
    }
  };

  const switchMode = (m: AiMode) => {
    if (m === mode) return;
    setMode(m);
    setError(null);
  };

  const manualHref = `/entry/new?meal=${encodeURIComponent(mealType)}&date=${queryDate}`;

  const runEstimate = useCallback(async (accumNotes: string[]) => {
    if (!user || pending) return; // single in-flight: no new request until this settles
    if (mode === 'photo' ? !imageBlob : !description.trim()) {
      setError(mode === 'photo' ? 'Take or choose a photo first.' : 'Describe what you ate first.');
      return;
    }

    setPending(true);
    setError(null);
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      let res: Response;
      if (mode === 'photo') {
        const fd = new FormData();
        fd.append('image', imageBlob!, 'meal.jpg');
        if (photoNote.trim()) fd.append('description', photoNote.trim());
        accumNotes.forEach((n) => fd.append('notes', n));
        res = await apiFetch(`/api/user/${user.id}/estimate/image`, {
          method: 'POST', body: fd, signal: controller.signal,
        });
      } else {
        res = await apiFetch(`/api/user/${user.id}/estimate/text`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ description: description.trim(), notes: accumNotes }),
          signal: controller.signal,
        });
      }
      const data = (await res.json()) as EstimateApiResponse;
      if (!data.ok) {
        setError(data.error || "Couldn't estimate — enter it manually.");
        return; // keep any prior rows (a refine that failed leaves the last result up)
      }
      // System-produced rows (AI estimate/refine) get normalized to the user's inferred
      // unit system before ratios are derived (§3b-R6) — manual input is never touched.
      const fresh = data.items.map((r) => ({
        ...withRatios(convertToSystem(r, preferredSystem)),
        key: nextKey(),
      }));
      setRows(fresh);
      setAiSnapshot(fresh); // baseline to undo manual edits back to
      setOverallConfidence(data.overallConfidence);
      setSource(data.source || (mode === 'photo' ? 'AiPhoto' : 'AiText'));
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return; // user cancelled — stay put
      setError("Couldn't reach the estimator — enter it manually.");
    } finally {
      setPending(false);
      abortRef.current = null;
    }
  }, [user, pending, mode, description, imageBlob, photoNote, preferredSystem]);

  const onEstimate = () => { setNotes([]); runEstimate([]); };

  const onRefine = () => {
    const note = noteDraft.trim();
    if (!note) return;
    const next = [...notes, note];
    setNotes(next);
    setNoteDraft('');
    runEstimate(next);
  };

  const cancel = () => abortRef.current?.abort();

  const updateRow = (key: string, patch: Partial<Row>) =>
    setRows((rs) => rs?.map((r) => (r.key === key ? { ...r, ...patch } : r)) ?? null);

  // Changing the amount rescales calories + macros from the row's per-unit ratios.
  const changeQuantity = (key: string, qty: number) =>
    setRows((rs) => rs?.map((r) => (r.key === key ? {
      ...r,
      quantity: qty,
      calories: Math.round(r.caloriesPerUnit * qty),
      protein: r.proteinPerUnit == null ? null : round1(r.proteinPerUnit * qty),
      carbs: r.carbsPerUnit == null ? null : round1(r.carbsPerUnit * qty),
      fat: r.fatPerUnit == null ? null : round1(r.fatPerUnit * qty),
    } : r)) ?? null);

  // A manual calories/macro edit re-derives that field's per-unit ratio, so a later
  // quantity change still scales proportionally from the corrected value.
  const changeCalories = (key: string, cal: number) =>
    setRows((rs) => rs?.map((r) => (r.key === key
      ? { ...r, calories: cal, caloriesPerUnit: perUnit(cal, r.quantity) } : r)) ?? null);
  const changeMacro = (key: string, field: 'protein' | 'carbs' | 'fat', val: number | null) =>
    setRows((rs) => rs?.map((r) => {
      if (r.key !== key) return r;
      const ratio = perUnitOpt(val, r.quantity);
      if (field === 'protein') return { ...r, protein: val, proteinPerUnit: ratio };
      if (field === 'carbs') return { ...r, carbs: val, carbsPerUnit: ratio };
      return { ...r, fat: val, fatPerUnit: ratio };
    }) ?? null);
  const deleteRow = (key: string) =>
    setRows((rs) => rs?.filter((r) => r.key !== key) ?? null);

  // Restore the AI's original suggestion (undo all manual edits/deletes), no re-estimate.
  const undoEdits = () => { if (aiSnapshot) setRows(aiSnapshot.map((r) => ({ ...r }))); };
  const editedFromAi =
    aiSnapshot != null && rows != null && JSON.stringify(rows) !== JSON.stringify(aiSnapshot);

  const optNum = (v: string): number | null => (v === '' ? null : Number(v));

  const save = async () => {
    if (!user || !rows || rows.length === 0) return;

    // Re-check for name collisions at save time, not just when the row was first shown —
    // the user may have edited a "new" row's name into an existing catalogue food's name
    // since (§3a-R5 item 2). Normalized the same way the backend resolves matches.
    const dup = rows.find((r) => r.isNew && catalogue.has(normalizeFoodName(r.name)));
    if (dup) {
      setError(`"${dup.name.trim()}" already exists in your catalogue — rename it or remove the row before saving.`);
      return;
    }

    setSaving(true);
    setError(null);
    saveLastMealType(mealType);
    const intakeAtUtc = new Date(intakeAt).toISOString();
    const body = {
      items: rows.map((r) => ({
        foodId: r.matchedFoodId, // null → backend defines the new food, then references it
        foodName: r.name,
        intakeAtUtc,
        mealType,
        quantity: r.quantity,
        uoM: r.uom,
        calories: r.calories,
        protein: r.protein,
        carbs: r.carbs,
        fat: r.fat,
        confidence: r.confidence,
        source,
      })),
    };
    try {
      const res = await apiFetch(`/api/user/${user.id}/entries/batch`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: 'Save failed' }));
        throw new Error(err.error || 'Save failed');
      }
      navigate('/');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Save failed.');
    } finally {
      setSaving(false);
    }
  };

  if (!user) return null;

  return (
    <div className="entry-form ai-entry">
      <h1>Describe with AI</h1>

      {aiEnabled === false && !barcodeEnabled ? (
        <div className="ai-disabled-notice">
          <p>AI estimation isn't configured on this server.</p>
          <Link to={manualHref} className="save-btn ai-manual-link">Enter it manually</Link>
        </div>
      ) : (
        <>
          {error && <p className="form-error" role="alert">{error}</p>}

          {[supportsText, supportsImages, barcodeEnabled].filter(Boolean).length > 1 && (
            <div className="ai-mode-toggle" role="tablist" aria-label="Input method">
              {supportsText && (
                <button
                  role="tab" type="button"
                  aria-selected={mode === 'text'}
                  className={mode === 'text' ? 'active' : ''}
                  onClick={() => switchMode('text')}
                  disabled={pending}
                >Text</button>
              )}
              {supportsImages && (
                <button
                  role="tab" type="button"
                  aria-selected={mode === 'photo'}
                  className={mode === 'photo' ? 'active' : ''}
                  onClick={() => switchMode('photo')}
                  disabled={pending}
                >Photo</button>
              )}
              {barcodeEnabled && (
                <button
                  role="tab" type="button"
                  aria-selected={mode === 'barcode'}
                  className={mode === 'barcode' ? 'active' : ''}
                  onClick={() => switchMode('barcode')}
                  disabled={pending}
                >Barcode</button>
              )}
            </div>
          )}

          {mode === 'text' ? (
            <div className="form-section">
              <label htmlFor="ai-desc">
                What did you eat?
                <span className="ai-lang-info" title="English works best. Other Latin-alphabet languages (German, Serbian, French, etc.) are usually fine too. Cyrillic and transcribed Cyrillic may miss items — if something isn't recognised, refine with a note, or edit the name below after estimating."> ℹ️</span>
              </label>
              <textarea
                id="ai-desc"
                className="ai-desc-input"
                rows={3}
                placeholder="e.g. chicken breast 200g with a cup of cooked rice and a side salad"
                value={description}
                onChange={(e) => setDescription(e.target.value)}
                disabled={pending}
              />
            </div>
          ) : mode === 'photo' ? (
            <div className="form-section ai-photo">
              <label>Snap or upload a photo of your meal</label>

              {imageUrl ? (
                <div className="ai-photo-preview">
                  <img src={imageUrl} alt="Meal to estimate" className="ai-photo-img" />
                  <button type="button" className="cancel-btn" onClick={clearImage} disabled={pending}>Retake / remove</button>
                </div>
              ) : (
                <div className="ai-photo-actions single">
                  <PhotoPickButton label="Take or upload a photo" onFile={useImage} />
                </div>
              )}

              <p className="ai-photo-hint">Your photo is sent for estimation and never stored — it stays in this browser until you save or leave.</p>

              <div className="ai-photo-note">
                <label htmlFor="ai-photo-note">Add a note to guide recognition (optional)</label>
                <textarea
                  id="ai-photo-note"
                  className="ai-desc-input"
                  rows={2}
                  placeholder="e.g. the sauce is olive-oil based, the portion is large, includes 2 fried eggs"
                  value={photoNote}
                  onChange={(e) => setPhotoNote(e.target.value)}
                  disabled={pending}
                />
              </div>
            </div>
          ) : (
            <div className="form-section ai-barcode">
              <label className="ai-barcode-label">Snap or upload a photo of a barcode (EAN/UPC)</label>

              <div className="ai-photo-actions single">
                <PhotoPickButton
                  label="Take or upload a barcode photo"
                  onFile={onBarcodeFile}
                  disabled={barcodeBusy}
                />
              </div>

              <div className="ai-barcode-manual">
                <input
                  type="text"
                  inputMode="numeric"
                  placeholder="Barcode digits (8–14)"
                  value={barcodeCode}
                  onChange={(e) => setBarcodeCode(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter') lookupBarcode(barcodeCode); }}
                  disabled={barcodeBusy}
                />
                <CheckButton label="Look up barcode" busy={barcodeBusy} onClick={() => lookupBarcode(barcodeCode)} disabled={barcodeBusy || !barcodeCode.trim()} />
              </div>

              {barcodeBusy && <p className="ai-barcode-hint">Reading barcode…</p>}
              {barcodeMsg && <p className="ai-barcode-msg" role="alert">{barcodeMsg}</p>}
            </div>
          )}

          {pending ? (
            <div className="ai-pending" role="status">
              <span className="ai-spinner" aria-hidden="true" />
              <span>Estimating…</span>
              <button className="cancel-btn" onClick={cancel}>Cancel</button>
            </div>
          ) : (mode === 'text' || (mode === 'photo' && supportsImages)) && (
            <div className="ai-estimate-row">
              <CheckButton
                label={rows ? 'Re-estimate' : 'Estimate'}
                onClick={onEstimate}
                disabled={mode === 'photo' ? !imageBlob : !description.trim()}
              />
            </div>
          )}

          <p className="ai-manual-fallback">
            Prefer to type it yourself? <Link to={manualHref}>Enter manually</Link>
          </p>

          {/* Refining re-runs the estimate, so only for AI results (text or photo) — a
              scanned barcode product has nothing to re-estimate. Shown whenever an estimate
              attempt has been made — including a FAILED one (rows null, error set) — so a bad
              attempt can be clarified without scrolling back up to the photo-note field
              (§3a-R3). Anchored here (above the row list, not below it) so it stays reachable
              on a phone regardless of how many rows a successful estimate returns. */}
          {(mode === 'text' || imageBlob) && (rows || error) && (
            <div className="ai-refine">
              <label htmlFor="ai-note">Not quite right? Add a clarification</label>
              <div className="ai-refine-row">
                <input
                  id="ai-note"
                  value={noteDraft}
                  placeholder="e.g. the rice was 1.5 cups, the bread is wholemeal"
                  onChange={(e) => setNoteDraft(e.target.value)}
                  onKeyDown={(e) => { if (e.key === 'Enter') onRefine(); }}
                  disabled={pending}
                />
                <button className="cancel-btn" onClick={onRefine} disabled={pending || !noteDraft.trim()}>Refine</button>
              </div>
            </div>
          )}

          {rows && (
            <div className="ai-review">
              <div className="ai-review-head">
                <h2>Review {rows.length} item{rows.length === 1 ? '' : 's'}</h2>
                {overallConfidence != null && (
                  <span className="ai-confidence">{Math.round(overallConfidence * 100)}% overall</span>
                )}
                {editedFromAi && (
                  <button type="button" className="ai-undo-btn" onClick={undoEdits}
                    title="Restore the AI's original suggestion">↺ Undo edits</button>
                )}
              </div>

              {rows.length === 0 && <p className="settings-muted">No items left — add a note and refine, or enter manually.</p>}

              {rows.map((r) => (
                <div key={r.key} className={`ai-row${r.confidence < LOW_CONFIDENCE ? ' low-confidence' : ''}${r.isNew ? ' unmatched' : ''}`}>
                  <div className="ai-row-top">
                    <input
                      className={`ai-row-name${r.isNew ? ' unmatched-name' : ''}`}
                      value={r.name}
                      onChange={(e) => updateRow(r.key, { name: e.target.value })}
                      aria-label="Food name"
                    />
                    <div className="ai-row-badges">
                      {r.isNew && <span className="ai-badge ai-badge-new" title="Added to your catalogue when you save">new</span>}
                      <span className="ai-badge ai-badge-conf">{Math.round(r.confidence * 100)}%</span>
                      <button className="entry-row-btn danger" onClick={() => deleteRow(r.key)} aria-label={`Remove ${r.name}`}>Del</button>
                    </div>
                  </div>
                  {r.isNew && (
                    catalogue.has(normalizeFoodName(r.name)) ? (
                      <div className="ai-unmatched-hint dup">
                        ⚠ "{r.name.trim()}" already exists in your catalogue — saving will create a duplicate. Rename it, or remove this row and search for the existing food.
                      </div>
                    ) : (
                      <div className="ai-unmatched-hint">Not matched to your catalogue — edit the name above if needed</div>
                    )
                  )}
                  <div className="ai-row-fields">
                    <label>Qty
                      <NumberInput min="0" value={r.quantity}
                        onValueChange={(v) => changeQuantity(r.key, v ?? 0)} />
                    </label>
                    <label>Unit
                      <UnitSelect value={r.uom} onChange={(v) => updateRow(r.key, { uom: v })} />
                    </label>
                    <label>Cal
                      <NumberInput value={r.calories}
                        onValueChange={(v) => changeCalories(r.key, v ?? 0)} />
                    </label>
                    {showMacros && (
                      <>
                        <label>P
                          <input type="number" value={r.protein ?? ''}
                            onChange={(e) => changeMacro(r.key, 'protein', optNum(e.target.value))} />
                        </label>
                        <label>C
                          <input type="number" value={r.carbs ?? ''}
                            onChange={(e) => changeMacro(r.key, 'carbs', optNum(e.target.value))} />
                        </label>
                        <label>F
                          <input type="number" value={r.fat ?? ''}
                            onChange={(e) => changeMacro(r.key, 'fat', optNum(e.target.value))} />
                        </label>
                      </>
                    )}
                  </div>
                </div>
              ))}

              <div className="entry-form-row" style={{ marginTop: '1rem' }}>
                <div className="form-section" style={{ marginBottom: 0 }}>
                  <label>Meal</label>
                  <select value={mealType} onChange={(e) => setMealType(e.target.value)}>
                    {MEAL_OPTIONS.map((m) => <option key={m} value={m}>{m}</option>)}
                  </select>
                </div>
                <div className="form-section" style={{ marginBottom: 0 }}>
                  <label>When</label>
                  <input type="datetime-local" value={intakeAt} onChange={(e) => setIntakeAt(e.target.value)} />
                </div>
              </div>

              {rows.some((r) => r.isNew) && (
                <p className="ai-new-foods-disclosure">
                  Saving will add {rows.filter((r) => r.isNew).length} new food
                  {rows.filter((r) => r.isNew).length === 1 ? '' : 's'} to the catalogue:{' '}
                  {rows.filter((r) => r.isNew).map((r) => r.name.trim()).join(', ')}
                </p>
              )}

              <div className="form-actions">
                <button className="cancel-btn" onClick={() => navigate(-1)}>Cancel</button>
                <button className="save-btn" onClick={save} disabled={saving || pending || rows.length === 0}>
                  {saving ? 'Saving…' : `Save ${rows.length} item${rows.length === 1 ? '' : 's'}`}
                </button>
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}

export default AiEntryPage;
