import { useState, useEffect, useMemo, useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getLastMealType, saveLastMealType } from '../lib/storage';
import { loadCatalogueByName, type CatalogueFood } from '../lib/foods';
import { useShowMacros } from '../hooks/useShowMacros';
import { useAiStatus } from '../hooks/useAiStatus';
import { useBarcodeStatus } from '../hooks/useBarcodeStatus';
import { inferPreferredSystem } from '../lib/units';
import { UnitSelect } from '../components/UnitSelect';
import { NumberInput } from '../components/NumberInput';
import {
  AiInputPanel,
  type EstimateItem,
  type EstimateMeta,
  type BarcodeFood,
} from '../components/AiInputPanel';
import '../styles/entryform.css';
import '../styles/aientry.css';

const MEAL_OPTIONS = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];
const LOW_CONFIDENCE = 0.5;

interface Row extends EstimateItem {
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

// Attach per-unit ratios to an estimate row, derived from its own quantity.
const withRatios = (r: EstimateItem): Omit<Row, 'key'> => ({
  ...r,
  caloriesPerUnit: perUnit(r.calories, r.quantity),
  proteinPerUnit: perUnitOpt(r.protein, r.quantity),
  carbsPerUnit: perUnitOpt(r.carbs, r.quantity),
  fatPerUnit: perUnitOpt(r.fat, r.quantity),
});

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
  const { barcodeEnabled, barcodeStatusKnown } = useBarcodeStatus();

  const [rows, setRows] = useState<Row[] | null>(null);
  // Snapshot of the AI's suggestion as returned, so manual edits can be undone back to it
  // without re-running the estimate.
  const [aiSnapshot, setAiSnapshot] = useState<Row[] | null>(null);
  const [overallConfidence, setOverallConfidence] = useState<number | null>(null);
  const [source, setSource] = useState('AiText');

  // Catalogue keyed by lowercased name — to flag a "new" row that actually duplicates
  // an existing food (we don't auto-merge yet; just warn so we stop minting dupes).
  const [catalogue, setCatalogue] = useState<Map<string, CatalogueFood>>(new Map());
  // Inferred from the user's own catalogue usage (docs/food-catalogue-and-logging.md
  // §Unit-system inference) — recomputed per catalogue load, never persisted. The
  // AiInputPanel converts estimate rows to this system before we see them.
  const preferredSystem = useMemo(
    () => inferPreferredSystem(Array.from(catalogue.values())),
    [catalogue],
  );

  const [mealType, setMealType] = useState(queryMeal);
  const [intakeAt, setIntakeAt] = useState(() => `${queryDate}T${nowTime()}`);

  const [estimating, setEstimating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Load the catalogue once for duplicate-name detection on "new" review rows, and to
  // infer the user's preferred unit system (usageCount per food) for row normalization.
  useEffect(() => {
    if (!user) return;
    let alive = true;
    loadCatalogueByName(user.id).then((m) => { if (alive) setCatalogue(m); });
    return () => { alive = false; };
  }, [user?.id]);

  const handleResult = useCallback((items: EstimateItem[], meta: EstimateMeta) => {
    const fresh = items.map((r) => ({ ...withRatios(r), key: nextKey() }));
    setRows(fresh);
    setAiSnapshot(fresh); // baseline to undo manual edits back to
    setOverallConfidence(meta.overallConfidence);
    setSource(meta.source);
  }, []);

  // A new photo was chosen in the panel — the prior review no longer applies.
  const handleReset = useCallback(() => {
    setRows(null);
    setAiSnapshot(null);
    setOverallConfidence(null);
    setError(null);
  }, []);

  // A resolved barcode product is already a real catalogue food (the backend caches it),
  // so it joins the review list as a matched row — not a "new" one.
  const addBarcodeFood = useCallback((food: BarcodeFood) => {
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
  }, []);

  const manualHref = `/entry/new?meal=${encodeURIComponent(mealType)}&date=${queryDate}`;

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
    // since. Normalized the same way the backend resolves matches.
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

          {/* Mounted once both capability fetches have settled — the panel picks its
              starting tab from the flags at mount. */}
          {aiEnabled !== null && barcodeStatusKnown && (
            <AiInputPanel
              userId={user.id}
              kind="meal"
              supportsText={supportsText}
              supportsImages={supportsImages}
              barcodeEnabled={barcodeEnabled}
              preferredSystem={preferredSystem}
              onResult={handleResult}
              onBarcodeFood={addBarcodeFood}
              onReset={handleReset}
              onPendingChange={setEstimating}
            />
          )}

          <p className="ai-manual-fallback">
            Prefer to type it yourself? <Link to={manualHref}>Enter manually</Link>
          </p>

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
                <button className="save-btn" onClick={save} disabled={saving || estimating || rows.length === 0}>
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
