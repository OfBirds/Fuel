import { useState, useRef, useCallback, useEffect, useMemo } from 'react';
import { apiFetch } from '../lib/api';
import { useAiStatus } from '../hooks/useAiStatus';
import { normalizeImage } from '../lib/image';
import { loadCatalogueByName, type CatalogueFood } from '../lib/foods';
import { convertToSystem, inferPreferredSystem, refQty } from '../lib/units';
import { CheckButton } from '../components/CheckButton';
import { PhotoPickButton } from '../components/PhotoPickButton';
import type { FoodFormData } from '../pages/CataloguePage';
import '../styles/aientry.css';

type AiMode = 'text' | 'photo';

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

interface DisplayRow extends ApiRow {
  key: string;
}

interface FoodAiAssistProps {
  userId: string;
  onApply: (prefill: Partial<FoodFormData> & { matchedFoodId: string | null }) => void;
}

let keySeq = 0;
const nextKey = () => `fa-${keySeq++}`;

const round2 = (n: number) => Math.round(n * 100) / 100;

export function FoodAiAssist({ userId, onApply }: FoodAiAssistProps) {
  const { aiEnabled, supportsText, supportsImages } = useAiStatus();

  const [mode, setMode] = useState<AiMode>('text');
  const [description, setDescription] = useState('');
  const [rows, setRows] = useState<DisplayRow[] | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [noteDraft, setNoteDraft] = useState('');

  // Photo state — mirrors AiEntryPage's useImage/clearImage lifecycle.
  const [imageBlob, setImageBlob] = useState<Blob | null>(null);
  const [imageUrl, setImageUrl] = useState<string | null>(null);

  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Catalogue + unit-system inference for row normalisation (§3b-R6).
  const [catalogue, setCatalogue] = useState<Map<string, CatalogueFood>>(new Map());
  const preferredSystem = useMemo(
    () => inferPreferredSystem(Array.from(catalogue.values())),
    [catalogue],
  );

  useEffect(() => {
    if (!aiEnabled) return;
    let alive = true;
    loadCatalogueByName(userId).then((m) => { if (alive) setCatalogue(m); });
    return () => { alive = false; };
  }, [userId, aiEnabled]);

  // Abort in-flight request + revoke object URL on unmount.
  useEffect(() => () => { abortRef.current?.abort(); }, []);
  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl); }, [imageUrl]);

  const useImage = async (blob: Blob) => {
    setRows(null); setNotes([]); setError(null);
    let normalized = blob;
    try { normalized = await normalizeImage(blob); } catch { /* fall back to original */ }
    setImageBlob(normalized);
    setImageUrl(URL.createObjectURL(normalized));
  };

  const clearImage = () => { setImageBlob(null); setImageUrl(null); };

  const switchMode = (m: AiMode) => {
    if (m === mode) return;
    setMode(m);
    setError(null);
  };

  // ── Estimate / Refine (mirrors AiEntryPage.runEstimate) ──

  const runEstimate = useCallback(async (accumNotes: string[]) => {
    if (pending) return;
    if (mode === 'photo' ? !imageBlob : !description.trim()) {
      setError(mode === 'photo' ? 'Take or choose a photo first.' : 'Describe the food first.');
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
        fd.append('image', imageBlob!, 'food.jpg');
        accumNotes.forEach((n) => fd.append('notes', n));
        res = await apiFetch(`/api/user/${userId}/estimate/image`, {
          method: 'POST', body: fd, signal: controller.signal,
        });
      } else {
        res = await apiFetch(`/api/user/${userId}/estimate/text`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ description: description.trim(), notes: accumNotes }),
          signal: controller.signal,
        });
      }
      const data = (await res.json()) as EstimateApiResponse;
      if (!data.ok) {
        setError(data.error || "Couldn't estimate — enter it manually.");
        return; // keep prior rows visible
      }
      // §3b-R6: system-produced rows → user's preferred unit system first, then render.
      const fresh: DisplayRow[] = data.items.map((r) => ({
        ...convertToSystem(r, preferredSystem),
        key: nextKey(),
      }));
      setRows(fresh);
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return;
      setError("Couldn't reach the estimator — enter it manually.");
    } finally {
      setPending(false);
      abortRef.current = null;
    }
  }, [pending, mode, description, imageBlob, userId, preferredSystem]);

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

  // ── Apply: convert the row's absolute-for-quantity values to reference-basis per-unit ──
  // §3a-R4: displayCalories = round(row.calories / row.quantity * refQty(row.uom), 2dp)
  // Same for protein/carbs/fat, guarded for null.

  const handleApply = (row: DisplayRow) => {
    const rq = refQty(row.uom);
    const qty = row.quantity > 0 ? row.quantity : 1;
    const toPerRef = (v: number | null) =>
      v == null ? undefined : round2(v / qty * rq);

    onApply({
      name: row.name,
      defaultUoM: row.uom,
      caloriesPerUnit: toPerRef(row.calories) ?? 0,
      proteinPerUnit: toPerRef(row.protein),
      carbsPerUnit: toPerRef(row.carbs),
      fatPerUnit: toPerRef(row.fat),
      matchedFoodId: row.matchedFoodId,
    });
  };

  // ── Gating ──
  // null (loading) → render nothing (avoid flash of disabled UI)
  // false → render nothing (AI not configured)
  if (aiEnabled === null || aiEnabled === false) return null;

  const showModeToggle = supportsText && supportsImages;

  return (
    <div className="food-ai-assist">
      {error && <p className="form-error" role="alert">{error}</p>}

      {showModeToggle && (
        <div className="ai-mode-toggle" role="tablist" aria-label="Input method">
          <button
            role="tab" type="button"
            aria-selected={mode === 'text'}
            className={mode === 'text' ? 'active' : ''}
            onClick={() => switchMode('text')}
            disabled={pending}
          >Text</button>
          <button
            role="tab" type="button"
            aria-selected={mode === 'photo'}
            className={mode === 'photo' ? 'active' : ''}
            onClick={() => switchMode('photo')}
            disabled={pending}
          >Photo</button>
        </div>
      )}

      {mode === 'text' ? (
        <div className="food-form-section">
          <label htmlFor="fa-desc">Describe the food</label>
          <textarea
            id="fa-desc"
            className="ai-desc-input"
            rows={2}
            placeholder="e.g. cooked chicken breast, raw, boneless skinless"
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            disabled={pending}
          />
        </div>
      ) : (
        <div className="form-section ai-photo">
          <label>Snap or upload a photo of the food packaging or label</label>

          {imageUrl ? (
            <div className="ai-photo-preview">
              <img src={imageUrl} alt="Food to estimate" className="ai-photo-img" />
              <button type="button" className="cancel-btn" onClick={clearImage} disabled={pending}>Retake / remove</button>
            </div>
          ) : (
            <div className="ai-photo-actions single">
              <PhotoPickButton label="Take or upload a photo" onFile={useImage} />
            </div>
          )}
        </div>
      )}

      {pending ? (
        <div className="ai-pending" role="status">
          <span className="ai-spinner" aria-hidden="true" />
          <span>Estimating…</span>
          <button className="cancel-btn" onClick={cancel}>Cancel</button>
        </div>
      ) : (
        <div className="ai-estimate-row">
          <CheckButton
            label={rows ? 'Re-estimate' : 'Estimate'}
            onClick={onEstimate}
            disabled={mode === 'photo' ? !imageBlob : !description.trim()}
          />
        </div>
      )}

      {/* Refine loop — visible after any estimate attempt (success or failure) */}
      {(mode === 'text' || imageBlob) && (rows || error) && (
        <div className="ai-refine">
          <label htmlFor="fa-note">Not quite right? Add a clarification</label>
          <div className="ai-refine-row">
            <input
              id="fa-note"
              value={noteDraft}
              placeholder="e.g. it's a thigh not a breast, the label lists 12g fat"
              onChange={(e) => setNoteDraft(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter') onRefine(); }}
              disabled={pending}
            />
            <button className="cancel-btn" onClick={onRefine} disabled={pending || !noteDraft.trim()}>Refine</button>
          </div>
        </div>
      )}

      {/* Results — each row rendered with its own Apply CheckButton */}
      {rows && rows.length > 0 && (
        <div className="fa-results">
          {rows.map((r) => (
            <div key={r.key} className="ai-row">
              <div className="ai-row-top">
                <span className="fa-row-name">{r.name}</span>
                <div className="ai-row-badges">
                  <span className="ai-badge ai-badge-conf">{Math.round(r.confidence * 100)}%</span>
                </div>
              </div>
              <div className="fa-row-detail">
                {r.quantity} {r.uom}
                {' · '}{r.calories} cal
                {r.protein != null ? ` · P ${r.protein}g` : ''}
                {r.carbs != null ? ` · C ${r.carbs}g` : ''}
                {r.fat != null ? ` · F ${r.fat}g` : ''}
              </div>
              <div className="fa-row-apply">
                <CheckButton
                  label={`Apply ${r.name}`}
                  onClick={() => handleApply(r)}
                />
              </div>
            </div>
          ))}
        </div>
      )}

      {rows && rows.length === 0 && !pending && (
        <p className="settings-muted">No items returned — try refining with a note.</p>
      )}
    </div>
  );
}
