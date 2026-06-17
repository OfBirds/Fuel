import { useState, useRef, useCallback, useEffect } from 'react';
import { apiFetch } from '../lib/api';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getLastMealType, saveLastMealType } from '../lib/storage';
import { normalizeImage } from '../lib/image';
import { loadCatalogueByName, type CatalogueFood } from '../lib/foods';
import { useShowMacros } from '../hooks/useShowMacros';
import { UnitSelect } from '../components/UnitSelect';
import '../styles/entryform.css';
import '../styles/aientry.css';

const MEAL_OPTIONS = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];
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

  const [aiEnabled, setAiEnabled] = useState<boolean | null>(null);
  const [supportsText, setSupportsText] = useState(false);
  const [supportsImages, setSupportsImages] = useState(false);
  const [mode, setMode] = useState<'text' | 'photo'>('text');
  const [description, setDescription] = useState('');
  const [rows, setRows] = useState<Row[] | null>(null);
  const [notes, setNotes] = useState<string[]>([]);
  const [noteDraft, setNoteDraft] = useState('');
  const [overallConfidence, setOverallConfidence] = useState<number | null>(null);
  const [source, setSource] = useState('AiText');

  // Photo capture: the blob lives in browser memory for this review only and is
  // re-sent on each refine turn — never persisted (docs/ai-estimation.md §Image lifetime).
  const [imageBlob, setImageBlob] = useState<Blob | null>(null);
  const [imageUrl, setImageUrl] = useState<string | null>(null);
  const [cameraOn, setCameraOn] = useState(false);
  const videoRef = useRef<HTMLVideoElement | null>(null);
  const streamRef = useRef<MediaStream | null>(null);

  // Barcode (EAN/UPC) scan — lives inside Photo mode alongside camera/upload.
  // It's a precise digit decode (ZXing) + Open Food Facts lookup, not an AI guess,
  // so it stays a dedicated path; a hit becomes a matched review row.
  const [barcodeEnabled, setBarcodeEnabled] = useState(false);
  const [barcodeCameraOn, setBarcodeCameraOn] = useState(false);
  const [barcodeCode, setBarcodeCode] = useState('');
  const [barcodeBusy, setBarcodeBusy] = useState(false);
  const [barcodeMsg, setBarcodeMsg] = useState<string | null>(null);
  const barcodeVideoRef = useRef<HTMLVideoElement | null>(null);
  const barcodeStreamRef = useRef<MediaStream | null>(null);

  // Catalogue keyed by lowercased name — to flag a "new" row that actually duplicates
  // an existing food (we don't auto-merge yet; just warn so we stop minting dupes).
  const [catalogue, setCatalogue] = useState<Map<string, CatalogueFood>>(new Map());

  const [mealType, setMealType] = useState(queryMeal);
  const [intakeAt, setIntakeAt] = useState(() => `${queryDate}T${nowTime()}`);

  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  // AI affordances render per modality: the Describe input needs a text provider, the
  // Photo tab needs a vision provider. Each is configured independently in the chain.
  useEffect(() => {
    let alive = true;
    (async () => {
      const [aiRes, bcRes] = await Promise.allSettled([
        apiFetch('/api/ai/status'),
        apiFetch('/api/barcode/status'),
      ]);
      if (!alive) return;
      let aiOn = false, sText = false, sImg = false, bcOn = false;
      if (aiRes.status === 'fulfilled' && aiRes.value?.ok) {
        const s = await aiRes.value.json();
        aiOn = s.enabled === true;
        sText = s.supportsText === true;
        sImg = s.supportsImages === true;
      }
      if (bcRes.status === 'fulfilled' && bcRes.value?.ok) {
        bcOn = (await bcRes.value.json()).enabled === true;
      }
      if (!alive) return;
      setAiEnabled(aiOn);
      setSupportsText(sText);
      setSupportsImages(sImg);
      setBarcodeEnabled(bcOn);
      // No text input configured but photo/barcode is → start on the Photo tab.
      if (!sText && (sImg || bcOn)) setMode('photo');
    })();
    return () => { alive = false; };
  }, []);

  // Load the catalogue once for duplicate-name detection on "new" review rows.
  useEffect(() => {
    let alive = true;
    loadCatalogueByName().then((m) => { if (alive) setCatalogue(m); });
    return () => { alive = false; };
  }, []);

  // Abort any in-flight request and release both cameras if we leave the screen.
  useEffect(() => () => {
    abortRef.current?.abort();
    streamRef.current?.getTracks().forEach((t) => t.stop());
    barcodeStreamRef.current?.getTracks().forEach((t) => t.stop());
  }, []);

  // Revoke the previous object URL when the photo changes or the screen unmounts.
  useEffect(() => () => { if (imageUrl) URL.revokeObjectURL(imageUrl); }, [imageUrl]);

  const stopCamera = useCallback(() => {
    streamRef.current?.getTracks().forEach((t) => t.stop());
    streamRef.current = null;
    setCameraOn(false);
  }, []);

  // Attach the live stream once the <video> has mounted.
  useEffect(() => {
    if (cameraOn && videoRef.current && streamRef.current) {
      videoRef.current.srcObject = streamRef.current;
    }
  }, [cameraOn]);

  const useImage = async (blob: Blob) => {
    setRows(null); setNotes([]); setOverallConfidence(null); setError(null);
    // Re-encode to JPEG + downscale before it ever leaves the browser: HEIC and
    // multi-MB phone photos otherwise 400 at the vision provider (see lib/image).
    let normalized = blob;
    try { normalized = await normalizeImage(blob); } catch { /* fall back to original */ }
    setImageBlob(normalized);
    setImageUrl(URL.createObjectURL(normalized)); // prior URL revoked by the effect above
  };

  const secureCamera = () => typeof window !== 'undefined'
    && window.isSecureContext && !!navigator.mediaDevices?.getUserMedia;

  const startCamera = async () => {
    setError(null);
    if (!secureCamera()) {
      // getUserMedia is blocked outside a secure context (e.g. http on the LAN).
      setError('Camera needs a secure (HTTPS) connection — use “Choose a photo” to upload instead.');
      return;
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
      streamRef.current = stream;
      setCameraOn(true);
    } catch {
      // Permission denied / no camera → the upload fallback always works.
      setError('Camera unavailable — choose or take a photo with the file picker below.');
    }
  };

  const capturePhoto = () => {
    const video = videoRef.current;
    if (!video || !video.videoWidth) return;
    const canvas = document.createElement('canvas');
    canvas.width = video.videoWidth;
    canvas.height = video.videoHeight;
    canvas.getContext('2d')?.drawImage(video, 0, 0);
    canvas.toBlob((blob) => { if (blob) useImage(blob); }, 'image/jpeg', 0.9);
    stopCamera();
  };

  const onFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) useImage(file);
    e.target.value = ''; // allow re-selecting the same file
  };

  const clearImage = () => { setImageBlob(null); setImageUrl(null); };

  // ── Barcode scan (EAN/UPC) — its own camera/upload pair inside Photo mode ──
  const stopBarcodeScan = useCallback(() => {
    barcodeStreamRef.current?.getTracks().forEach((t) => t.stop());
    barcodeStreamRef.current = null;
    setBarcodeBusy(false);
    setBarcodeCameraOn(false);
  }, []);

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

  // Live camera scan: open the stream, let ZXing decode one barcode, then look it up.
  const startBarcodeScan = useCallback(async () => {
    setBarcodeMsg(null);
    setBarcodeCode('');
    if (!secureCamera()) {
      setBarcodeMsg('Camera needs a secure (HTTPS) connection — upload a photo or type the digits below.');
      return;
    }
    setBarcodeCameraOn(true);
    try {
      const { BrowserMultiFormatReader } = await import('@zxing/browser');
      const reader = new BrowserMultiFormatReader();
      const stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'environment', width: { ideal: 640 }, height: { ideal: 480 } },
        audio: false,
      });
      barcodeStreamRef.current = stream;
      const video = barcodeVideoRef.current;
      if (!video) { stream.getTracks().forEach((t) => t.stop()); setBarcodeCameraOn(false); return; }
      video.srcObject = stream;
      await new Promise<void>((resolve, reject) => {
        video.onloadedmetadata = () => { video.play().then(() => resolve(), reject); };
        video.onerror = () => reject(new Error('video'));
      });
      setBarcodeBusy(true);
      const result = await reader.decodeOnceFromVideoElement(video);
      if (result) await lookupBarcode(result.getText());
    } catch {
      // Denied / cancelled / no barcode → upload or manual entry still work.
    } finally {
      barcodeStreamRef.current?.getTracks().forEach((t) => t.stop());
      barcodeStreamRef.current = null;
      setBarcodeBusy(false);
      setBarcodeCameraOn(false);
    }
  }, [lookupBarcode]);

  // Upload path: decode a barcode straight from a chosen/snapped photo.
  const onBarcodeFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ''; // allow re-selecting the same file
    if (!file) return;
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

  const switchMode = (m: 'text' | 'photo') => {
    if (m === mode) return;
    if (m === 'text') { stopCamera(); stopBarcodeScan(); }
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
      setRows(data.items.map((r) => ({ ...r, key: nextKey() })));
      setOverallConfidence(data.overallConfidence);
      setSource(data.source || (mode === 'photo' ? 'AiPhoto' : 'AiText'));
    } catch (e) {
      if (e instanceof DOMException && e.name === 'AbortError') return; // user cancelled — stay put
      setError("Couldn't reach the estimator — enter it manually.");
    } finally {
      setPending(false);
      abortRef.current = null;
    }
  }, [user, pending, mode, description, imageBlob]);

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

          {supportsText && (supportsImages || barcodeEnabled) && (
            <div className="ai-mode-toggle" role="tablist" aria-label="Input method">
              <button
                role="tab" type="button"
                aria-selected={mode === 'text'}
                className={mode === 'text' ? 'active' : ''}
                onClick={() => switchMode('text')}
                disabled={pending}
              >Describe</button>
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
          ) : (
            <div className="form-section ai-photo">
              {supportsImages && (
                <>
                  <label>Snap or upload a photo of your meal</label>

                  {cameraOn ? (
                    <div className="ai-camera">
                      <video ref={videoRef} autoPlay playsInline muted className="ai-camera-preview" />
                      <div className="ai-photo-actions">
                        <button type="button" className="save-btn" onClick={capturePhoto}>Capture</button>
                        <button type="button" className="cancel-btn" onClick={stopCamera}>Cancel</button>
                      </div>
                    </div>
                  ) : imageUrl ? (
                    <div className="ai-photo-preview">
                      <img src={imageUrl} alt="Meal to estimate" className="ai-photo-img" />
                      <button type="button" className="cancel-btn" onClick={clearImage} disabled={pending}>Retake / remove</button>
                    </div>
                  ) : (
                    <div className="ai-photo-actions">
                      <button type="button" className="save-btn" onClick={startCamera}>Use camera</button>
                      <label className="cancel-btn ai-upload-btn">
                        Upload a file
                        <input type="file" accept="image/*" capture="environment" onChange={onFile} hidden aria-label="Upload a meal photo" />
                      </label>
                    </div>
                  )}

                  <p className="ai-photo-hint">Your photo is sent for estimation and never stored — it stays in this browser until you save or leave.</p>
                </>
              )}

              {barcodeEnabled && (
                <div className="ai-barcode">
                  {supportsImages && <div className="ai-barcode-sep"><span>or</span></div>}
                  <label className="ai-barcode-label">Snap or upload a photo of a barcode (EAN/UPC)</label>

                  {barcodeCameraOn ? (
                    <div className="ai-barcode-camera">
                      <video ref={barcodeVideoRef} autoPlay playsInline muted className="ai-camera-preview" />
                      <p className="ai-barcode-hint">Hold the barcode steady in the frame…</p>
                      <div className="ai-photo-actions">
                        <button type="button" className="bc-btn-outline" onClick={stopBarcodeScan}>Cancel</button>
                      </div>
                    </div>
                  ) : (
                    <div className="ai-photo-actions">
                      <button type="button" className="bc-btn" onClick={startBarcodeScan} disabled={pending || barcodeBusy}>
                        Use camera
                      </button>
                      <label className="bc-btn-outline ai-upload-btn">
                        Upload a file
                        <input type="file" accept="image/*" capture="environment" onChange={onBarcodeFile} hidden aria-label="Upload a barcode photo" />
                      </label>
                    </div>
                  )}

                  <details className="ai-barcode-typed">
                    <summary>Or type the digits</summary>
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
                      <button type="button" className="bc-btn" onClick={() => lookupBarcode(barcodeCode)} disabled={barcodeBusy || !barcodeCode.trim()}>
                        {barcodeBusy ? 'Looking…' : 'Look up'}
                      </button>
                    </div>
                  </details>

                  {barcodeBusy && !barcodeCameraOn && <p className="ai-barcode-hint">Reading barcode…</p>}
                  {barcodeMsg && <p className="ai-barcode-msg" role="alert">{barcodeMsg}</p>}
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
          ) : (mode === 'text' || (mode === 'photo' && supportsImages)) && !cameraOn && (
            <div className="ai-estimate-row">
              <button className="save-btn ai-estimate-btn" onClick={onEstimate} disabled={mode === 'photo' && !imageBlob}>
                {rows ? 'Re-estimate' : 'Estimate'}
              </button>
            </div>
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
                    catalogue.has(r.name.trim().toLowerCase()) ? (
                      <div className="ai-unmatched-hint dup">
                        ⚠ "{r.name.trim()}" already exists in your catalogue — saving will create a duplicate. Rename it, or remove this row and search for the existing food.
                      </div>
                    ) : (
                      <div className="ai-unmatched-hint">Not matched to your catalogue — edit the name above if needed</div>
                    )
                  )}
                  <div className="ai-row-fields">
                    <label>Qty
                      <input type="number" min="0" value={r.quantity}
                        onChange={(e) => updateRow(r.key, { quantity: num(e.target.value) })} />
                    </label>
                    <label>Unit
                      <UnitSelect value={r.uom} onChange={(v) => updateRow(r.key, { uom: v })} />
                    </label>
                    <label>Cal
                      <input type="number" value={r.calories}
                        onChange={(e) => updateRow(r.key, { calories: num(e.target.value) })} />
                    </label>
                    {showMacros && (
                      <>
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
                      </>
                    )}
                  </div>
                </div>
              ))}

              {/* Refining re-runs the estimate, so only for AI results (text or photo) —
                  a scanned barcode product has nothing to re-estimate. */}
              {(mode === 'text' || imageBlob) && (
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
