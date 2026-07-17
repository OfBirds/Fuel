import { useState, useRef, useCallback, useEffect } from 'react';
import { apiFetch } from '../lib/api';
import { normalizeImage } from '../lib/image';
import { convertToSystem } from '../lib/units';
import { CheckButton } from './CheckButton';
import { PhotoPickButton } from './PhotoPickButton';
import '../styles/entryform.css';
import '../styles/aientry.css';

export type AiMode = 'text' | 'photo' | 'barcode';

// One estimate row as returned by /api/user/{id}/estimate/{text|image}, already
// normalized to the user's preferred unit system when handed to onResult.
export interface EstimateItem {
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

export interface EstimateMeta {
  overallConfidence: number;
  source: string;
}

// Shape of the catalogue food returned by /api/barcode/lookup (FoodResponse).
export interface BarcodeFood {
  id: string;
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
  proteinPerUnit: number | null;
  carbsPerUnit: number | null;
  fatPerUnit: number | null;
}

interface EstimateApiResponse {
  ok: boolean;
  error: string | null;
  overallConfidence: number;
  source: string;
  items: EstimateItem[];
}

// The two hosts (diary AI entry, catalogue food form) share this control verbatim —
// tabs, photo picker, estimate/refine loop. Only the words differ.
const COPY = {
  meal: {
    textLabel: 'What did you eat?',
    textInfo: true,
    textPlaceholder: 'e.g. chicken breast 200g with a cup of cooked rice and a side salad',
    textRows: 3,
    describeFirst: 'Describe what you ate first.',
    photoLabel: 'Snap or upload a photo of your meal',
    photoAlt: 'Meal to estimate',
    imageName: 'meal.jpg',
    refinePlaceholder: 'e.g. the rice was 1.5 cups, the bread is wholemeal',
  },
  food: {
    textLabel: 'Describe the food',
    textInfo: false,
    textPlaceholder: 'e.g. cooked chicken breast, raw, boneless skinless',
    textRows: 2,
    describeFirst: 'Describe the food first.',
    photoLabel: 'Snap or upload a photo of the food packaging or label',
    photoAlt: 'Food to estimate',
    imageName: 'food.jpg',
    refinePlaceholder: "e.g. it's a thigh not a breast, the label lists 12g fat",
  },
} as const;

interface AiInputPanelProps {
  userId: string;
  kind: 'meal' | 'food';
  supportsText: boolean;
  supportsImages: boolean;
  barcodeEnabled: boolean;
  // Inferred from the user's catalogue usage (docs/food-catalogue-and-logging.md
  // §Unit-system inference) — estimate rows are converted to this system before
  // onResult fires. Manual input downstream is never touched.
  preferredSystem: 'metric' | 'imperial';
  onResult: (items: EstimateItem[], meta: EstimateMeta) => void;
  onBarcodeFood: (food: BarcodeFood) => void;
  // A new photo was chosen — stale results downstream should be cleared.
  onReset?: () => void;
  onPendingChange?: (pending: boolean) => void;
}

// The shared "describe it to the AI" top control. Hosts must mount this only once the
// capability fetches (useAiStatus + useBarcodeStatus) have settled — the starting tab is
// picked from the props at mount and not revisited.
export function AiInputPanel({
  userId, kind, supportsText, supportsImages, barcodeEnabled, preferredSystem,
  onResult, onBarcodeFood, onReset, onPendingChange,
}: AiInputPanelProps) {
  const copy = COPY[kind];

  const [mode, setMode] = useState<AiMode>(() =>
    supportsText ? 'text' : supportsImages ? 'photo' : barcodeEnabled ? 'barcode' : 'text');
  const [description, setDescription] = useState('');
  const [notes, setNotes] = useState<string[]>([]);
  const [noteDraft, setNoteDraft] = useState('');
  // Whether any estimate has succeeded yet — drives the Estimate/Re-estimate label and
  // the refine box (which also shows after a FAILED attempt, so a bad estimate can be
  // clarified in place).
  const [gotResult, setGotResult] = useState(false);

  // Photo capture: the blob lives in browser memory for this review only and is
  // re-sent on each refine turn — never persisted (docs/ai-estimation.md §Image lifetime).
  const [imageBlob, setImageBlob] = useState<Blob | null>(null);
  const [imageUrl, setImageUrl] = useState<string | null>(null);
  // Optional free-text hint the user attaches to the photo — rides with the FIRST estimate
  // (and every re-send) so the model weighs it alongside what it sees. Refine notes are separate.
  const [photoNote, setPhotoNote] = useState('');

  // Barcode (EAN/UPC) scan — its own tab. A precise digit decode (ZXing) run locally on the
  // captured/chosen photo + Open Food Facts lookup, not an AI guess.
  const [barcodeCode, setBarcodeCode] = useState('');
  const [barcodeBusy, setBarcodeBusy] = useState(false);
  const [barcodeMsg, setBarcodeMsg] = useState<string | null>(null);

  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const setPendingNotify = useCallback((p: boolean) => {
    setPending(p);
    onPendingChange?.(p);
  }, [onPendingChange]);

  // Abort any in-flight request if the host screen unmounts us.
  useEffect(() => () => { abortRef.current?.abort(); }, []);

  // Revoke the previous object URL when the photo changes or we unmount.
  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl); }, [imageUrl]);

  const useImage = async (blob: Blob) => {
    setNotes([]);
    setError(null);
    setGotResult(false);
    onReset?.();
    // Re-encode to JPEG + downscale before it ever leaves the browser: HEIC and
    // multi-MB phone photos otherwise 400 at the vision provider (see lib/image).
    let normalized = blob;
    try { normalized = await normalizeImage(blob); } catch { /* fall back to original */ }
    setImageBlob(normalized);
    setImageUrl(URL.createObjectURL(normalized)); // prior URL revoked by the effect above
  };

  const clearImage = () => { setImageBlob(null); setImageUrl(null); };

  const switchMode = (m: AiMode) => {
    if (m === mode) return;
    setMode(m);
    setError(null);
  };

  const runEstimate = useCallback(async (accumNotes: string[]) => {
    if (pending) return; // single in-flight: no new request until this settles
    if (mode === 'photo' ? !imageBlob : !description.trim()) {
      setError(mode === 'photo' ? 'Take or choose a photo first.' : copy.describeFirst);
      return;
    }

    setPendingNotify(true);
    setError(null);
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      let res: Response;
      if (mode === 'photo') {
        const fd = new FormData();
        fd.append('image', imageBlob!, copy.imageName);
        if (photoNote.trim()) fd.append('description', photoNote.trim());
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
        return; // keep any prior results downstream (a failed refine leaves the last result up)
      }
      setGotResult(true);
      onResult(
        data.items.map((r) => convertToSystem(r, preferredSystem)),
        {
          overallConfidence: data.overallConfidence,
          source: data.source || (mode === 'photo' ? 'AiPhoto' : 'AiText'),
        },
      );
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return; // user cancelled — stay put
      setError("Couldn't reach the estimator — enter it manually.");
    } finally {
      setPendingNotify(false);
      abortRef.current = null;
    }
  }, [pending, mode, description, imageBlob, photoNote, userId, preferredSystem, copy, onResult, setPendingNotify]);

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

  // ── Barcode: decode locally from a captured/chosen photo (or typed digits), then look up ──

  const lookupBarcode = useCallback(async (code: string) => {
    const clean = code.replace(/[^0-9]/g, '');
    if (!clean) return;
    setBarcodeBusy(true);
    setBarcodeMsg(null);
    try {
      const res = await apiFetch(`/api/barcode/lookup/${encodeURIComponent(clean)}`);
      const data = await res.json();
      if (res.ok && data.found && data.food) {
        onBarcodeFood(data.food as BarcodeFood);
        setBarcodeCode('');
      } else {
        setBarcodeMsg(data.message || 'Product not found — describe it or enter it manually.');
      }
    } catch {
      setBarcodeMsg('Lookup failed — try again, or enter it manually.');
    } finally {
      setBarcodeBusy(false);
    }
  }, [onBarcodeFood]);

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

  return (
    <div className="ai-input-panel">
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
            {copy.textLabel}
            {copy.textInfo && (
              <span className="ai-lang-info" title="English works best. Other Latin-alphabet languages (German, Serbian, French, etc.) are usually fine too. Cyrillic and transcribed Cyrillic may miss items — if something isn't recognised, refine with a note, or edit the name below after estimating."> ℹ️</span>
            )}
          </label>
          <textarea
            id="ai-desc"
            className="ai-desc-input"
            rows={copy.textRows}
            placeholder={copy.textPlaceholder}
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            disabled={pending}
          />
        </div>
      ) : mode === 'photo' ? (
        <div className="form-section ai-photo">
          <label>{copy.photoLabel}</label>

          {imageUrl ? (
            <div className="ai-photo-preview">
              <img src={imageUrl} alt={copy.photoAlt} className="ai-photo-img" />
              <button type="button" className="cancel-btn" onClick={clearImage} disabled={pending}>Retake / remove</button>
            </div>
          ) : (
            <PhotoPickButton label="Photo" onFile={useImage} />
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

          <PhotoPickButton
            label="Barcode photo"
            onFile={onBarcodeFile}
            disabled={barcodeBusy}
          />

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
            label={gotResult ? 'Re-estimate' : 'Estimate'}
            onClick={onEstimate}
            disabled={mode === 'photo' ? !imageBlob : !description.trim()}
          />
        </div>
      )}

      {/* Refining re-runs the estimate, so only for AI results (text or photo) — a scanned
          barcode product has nothing to re-estimate. Shown whenever an estimate attempt has
          been made — including a FAILED one (no result, error set) — so a bad attempt can be
          clarified without retyping the description. */}
      {(mode === 'text' || imageBlob) && (gotResult || error) && (
        <div className="ai-refine">
          <label htmlFor="ai-note">Not quite right? Add a clarification</label>
          <div className="ai-refine-row">
            <input
              id="ai-note"
              value={noteDraft}
              placeholder={copy.refinePlaceholder}
              onChange={(e) => setNoteDraft(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter') onRefine(); }}
              disabled={pending}
            />
            <button className="cancel-btn" onClick={onRefine} disabled={pending || !noteDraft.trim()}>Refine</button>
          </div>
        </div>
      )}
    </div>
  );
}
