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
      try {
        const res = await fetch('/api/ai/status');
        if (alive && res.ok) {
          const status = await res.json();
          setAiEnabled(status.enabled === true);
          setSupportsText(status.supportsText === true);
          setSupportsImages(status.supportsImages === true);
          // If only photo is configured, start on the photo input.
          if (status.supportsText !== true && status.supportsImages === true) setMode('photo');
        } else if (alive) setAiEnabled(false);
      } catch {
        if (alive) setAiEnabled(false);
      }
    })();
    return () => { alive = false; };
  }, []);

  // Abort any in-flight request and release the camera if we leave the screen.
  useEffect(() => () => {
    abortRef.current?.abort();
    streamRef.current?.getTracks().forEach((t) => t.stop());
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

  const useImage = (blob: Blob) => {
    setImageBlob(blob);
    setImageUrl(URL.createObjectURL(blob)); // prior URL revoked by the effect above
    setRows(null); setNotes([]); setOverallConfidence(null); setError(null);
  };

  const startCamera = async () => {
    setError(null);
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
      streamRef.current = stream;
      setCameraOn(true);
    } catch {
      // Secure-context / permission / no-camera → the upload fallback always works.
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

  const switchMode = (m: 'text' | 'photo') => {
    if (m === mode) return;
    if (m === 'text') stopCamera();
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
        res = await fetch(`/api/user/${user.id}/estimate/image`, {
          method: 'POST', body: fd, signal: controller.signal,
        });
      } else {
        res = await fetch(`/api/user/${user.id}/estimate/text`, {
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
          <p>AI estimation isn't configured on this server.</p>
          <Link to={manualHref} className="save-btn ai-manual-link">Enter it manually</Link>
        </div>
      ) : (
        <>
          {error && <p className="form-error" role="alert">{error}</p>}

          {supportsText && supportsImages && (
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
                    Choose a photo
                    <input type="file" accept="image/*" capture="environment" onChange={onFile} hidden aria-label="Choose a photo" />
                  </label>
                </div>
              )}

              <p className="ai-photo-hint">Your photo is sent for estimation and never stored — it stays in this browser until you save or leave.</p>
            </div>
          )}

          {pending ? (
            <div className="ai-pending" role="status">
              <span className="ai-spinner" aria-hidden="true" />
              <span>Estimating…</span>
              <button className="cancel-btn" onClick={cancel}>Cancel</button>
            </div>
          ) : !cameraOn && (
            <button className="save-btn" onClick={onEstimate} disabled={mode === 'photo' && !imageBlob}>
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
                    <div className="ai-unmatched-hint">Not matched to your catalogue — edit the name above if needed</div>
                  )}
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
