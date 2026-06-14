import { useState, useRef, useCallback, useEffect } from 'react';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getLastMealType, saveLastMealType } from '../lib/storage';
import '../styles/entryform.css';
import '../styles/aientry.css';

const MEAL_OPTIONS = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];
const UOM_OPTIONS = ['g', 'ml', 'piece'];
const LOW_CONFIDENCE = 0.5;

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
}

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
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const queryMeal = searchParams.get('meal') || getLastMealType();
  const queryDate = searchParams.get('date') || localDate();

  const [aiEnabled, setAiEnabled] = useState<boolean | null>(null);
  const [description, setDescription] = useState('');
  const [rows, setRows] = useState<Row[] | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [noteDraft, setNoteDraft] = useState('');
  const [overallConfidence, setOverallConfidence] = useState<number | null>(null);

  const [mealType, setMealType] = useState(queryMeal);
  const [intakeAt, setIntakeAt] = useState(() => `${queryDate}T${nowTime()}`);

  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  // AI affordances render only when the operator has AI on.
  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const res = await fetch('/api/ai/status');
        if (alive && res.ok) setAiEnabled((await res.json()).enabled === true);
        else if (alive) setAiEnabled(false);
      } catch {
        if (alive) setAiEnabled(false);
      }
    })();
    return () => { alive = false; };
  }, []);

  // Abort any in-flight request if we leave the screen.
  useEffect(() => () => abortRef.current?.abort(), []);

  const manualHref = `/entry/new?meal=${encodeURIComponent(mealType)}&date=${queryDate}`;

  const runEstimate = useCallback(async (accumNotes: string[]) => {
    if (!user || pending) return; // single in-flight: no new request until this settles
    if (!description.trim()) { setError('Describe what you ate first.'); return; }

    setPending(true);
    setError(null);
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      const res = await fetch(`/api/user/${user.id}/estimate/text`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ description: description.trim(), notes: accumNotes }),
        signal: controller.signal,
      });
      const data = (await res.json()) as EstimateApiResponse;
      if (!data.ok) {
        setError(data.error || "Couldn't estimate — enter it manually.");
        return; // keep any prior rows (a refine that failed leaves the last result up)
      }
      setRows(data.items.map((r) => ({ ...r, key: nextKey() })));
      setOverallConfidence(data.overallConfidence);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return; // user cancelled — stay put
      setError("Couldn't reach the estimator — enter it manually.");
    } finally {
      setPending(false);
      abortRef.current = null;
    }
  }, [user, pending, description]);

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
  const deleteRow = (key: string) =>
    setRows((rs) => rs?.filter((r) => r.key !== key) ?? null);

  const num = (v: string): number => (v === '' ? 0 : Number(v));
  const optNum = (v: string): number | null => (v === '' ? null : Number(v));

  const save = async () => {
    if (!user || !rows || rows.length === 0) return;
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
        source: 'AiText',
      })),
    };
    try {
      const res = await fetch(`/api/user/${user.id}/entries/batch`, {
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

      {aiEnabled === false ? (
        <div className="ai-disabled-notice">
          <p>AI estimation is turned off on this server.</p>
          <Link to={manualHref} className="save-btn ai-manual-link">Enter it manually</Link>
        </div>
      ) : (
        <>
          {error && <p className="form-error" role="alert">{error}</p>}

          <div className="form-section">
            <label htmlFor="ai-desc">What did you eat?</label>
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

          {pending ? (
            <div className="ai-pending" role="status">
              <span className="ai-spinner" aria-hidden="true" />
              <span>Estimating…</span>
              <button className="cancel-btn" onClick={cancel}>Cancel</button>
            </div>
          ) : (
            <button className="save-btn" onClick={onEstimate}>
              {rows ? 'Re-estimate' : 'Estimate'}
            </button>
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
              </div>

              {rows.length === 0 && <p className="settings-muted">No items left — add a note and refine, or enter manually.</p>}

              {rows.map((r) => (
                <div key={r.key} className={`ai-row${r.confidence < LOW_CONFIDENCE ? ' low-confidence' : ''}`}>
                  <div className="ai-row-top">
                    <input
                      className="ai-row-name"
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
                  <div className="ai-row-fields">
                    <label>Qty
                      <input type="number" min="0" value={r.quantity}
                        onChange={(e) => updateRow(r.key, { quantity: num(e.target.value) })} />
                    </label>
                    <label>Unit
                      <select value={r.uom} onChange={(e) => updateRow(r.key, { uom: e.target.value })}>
                        {[...new Set([r.uom, ...UOM_OPTIONS])].map((u) => <option key={u} value={u}>{u}</option>)}
                      </select>
                    </label>
                    <label>Cal
                      <input type="number" value={r.calories}
                        onChange={(e) => updateRow(r.key, { calories: num(e.target.value) })} />
                    </label>
                    <label>P
                      <input type="number" value={r.protein ?? ''}
                        onChange={(e) => updateRow(r.key, { protein: optNum(e.target.value) })} />
                    </label>
                    <label>C
                      <input type="number" value={r.carbs ?? ''}
                        onChange={(e) => updateRow(r.key, { carbs: optNum(e.target.value) })} />
                    </label>
                    <label>F
                      <input type="number" value={r.fat ?? ''}
                        onChange={(e) => updateRow(r.key, { fat: optNum(e.target.value) })} />
                    </label>
                  </div>
                </div>
              ))}

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
