import { useEffect, useState, useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { useNavigate, useSearchParams, useParams } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getLastMealType, saveLastMealType } from '../lib/storage';
import '../styles/entryform.css';

interface FoodItem {
  id: string;
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
  ingredientCount: number;
  isComposite: boolean;
  ponder: number | null;
  usageCount: number | null;
  lastUsedAtUtc: string | null;
}

type SortMode = 'priority' | 'alphabetical' | 'most-used' | 'recent';

const SORT_LABELS: Record<SortMode, string> = {
  priority: 'Priority',
  alphabetical: 'A–Z',
  'most-used': 'Most-used',
  recent: 'Recent',
};

interface FoodDetail extends FoodItem {
  proteinPerUnit: number | null;
  carbsPerUnit: number | null;
  fatPerUnit: number | null;
}

interface EntryDetail {
  id: string;
  foodId: string | null;
  foodName: string;
  intakeAtUtc: string;
  mealType: string;
  quantity: number;
  uoM: string;
  calories: number;
  protein: number | null;
  carbs: number | null;
  fat: number | null;
}

const UOM_OPTIONS = ['g', 'ml', 'piece'];
const MEAL_OPTIONS = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];

function toLocalDatetime(utc: string): string {
  const d = new Date(utc);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function EntryFormPage() {
  const { user } = useAuth();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const { entryId } = useParams<{ entryId: string }>();
  const isEdit = Boolean(entryId);

  const queryMeal = searchParams.get('meal') || getLastMealType();
  const queryDate = searchParams.get('date') || new Date().toISOString().slice(0, 10);

  // Form state
  const [searchTerm, setSearchTerm] = useState('');
  const [searchResults, setSearchResults] = useState<FoodItem[]>([]);
  const [searching, setSearching] = useState(false);
  const [searchFocused, setSearchFocused] = useState(false);
  const [sortMode, setSortMode] = useState<SortMode>('priority');
  const [selectedFood, setSelectedFood] = useState<FoodDetail | null>(null);
  const [showInlineForm, setShowInlineForm] = useState(false);

  // Inline food definition
  const [inlineName, setInlineName] = useState('');
  const [inlineUoM, setInlineUoM] = useState('g');
  const [inlineCal, setInlineCal] = useState(0);
  const [inlineProtein, setInlineProtein] = useState<number | undefined>();
  const [inlineCarbs, setInlineCarbs] = useState<number | undefined>();
  const [inlineFat, setInlineFat] = useState<number | undefined>();

  // Entry fields
  const [quantity, setQuantity] = useState(100);
  const [uom, setUom] = useState('g');
  const [mealType, setMealType] = useState(queryMeal);
  const [intakeAtUtc, setIntakeAtUtc] = useState(() => {
    // Default to the day being viewed (queryDate) at the current time — so logging
    // for a past/other day lands on that day. The time stays fully editable below,
    // letting you record when you actually ate (e.g. half an hour earlier).
    const now = new Date();
    const pad = (n: number) => String(n).padStart(2, '0');
    return `${queryDate}T${pad(now.getHours())}:${pad(now.getMinutes())}`;
  });
  const [calories, setCalories] = useState(0);
  const [protein, setProtein] = useState<number | undefined>();
  const [carbs, setCarbs] = useState<number | undefined>();
  const [fat, setFat] = useState<number | undefined>();

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(isEdit);
  const [aiEnabled, setAiEnabled] = useState(false);

  // Meal-pause warning
  const [mealPauseWarning, setMealPauseWarning] = useState<{ hoursSinceLast: number; mealPauseHours: number } | null>(null);

  // Load existing entry for editing
  useEffect(() => {
    if (!entryId || !user) return;
    let alive = true;
    (async () => {
      try {
        const res = await apiFetch(`/api/user/${user.id}/entries/${entryId}`);
        if (!res.ok) throw new Error('load failed');
        const entry = (await res.json()) as EntryDetail;
        if (alive) {
          setQuantity(entry.quantity);
          setUom(entry.uoM);
          setMealType(entry.mealType);
          setIntakeAtUtc(toLocalDatetime(entry.intakeAtUtc));
          setCalories(entry.calories);
          setProtein(entry.protein ?? undefined);
          setCarbs(entry.carbs ?? undefined);
          setFat(entry.fat ?? undefined);
          setSelectedFood(entry.foodId ? {
            id: entry.foodId, name: entry.foodName, defaultUoM: entry.uoM,
            caloriesPerUnit: entry.calories / (entry.quantity || 1),
            proteinPerUnit: null, carbsPerUnit: null, fatPerUnit: null,
            ingredientCount: 0, isComposite: false,
            ponder: null, usageCount: null, lastUsedAtUtc: null,
          } : null);
        }
      } catch {
        if (alive) setError("Couldn't load entry.");
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [entryId, user]);

  // Offer the AI "describe it" path only when the operator has AI enabled.
  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        const res = await apiFetch('/api/ai/status');
        if (alive && res.ok) setAiEnabled((await res.json()).enabled === true);
      } catch { /* leave AI off */ }
    })();
    return () => { alive = false; };
  }, []);

  // Preselect a food by foodId (query param — e.g. linked from elsewhere).
  useEffect(() => {
    if (isEdit || !user) return;
    const foodId = searchParams.get('foodId');
    if (!foodId) return;
    let alive = true;
    (async () => {
      try {
        const res = await apiFetch(`/api/foods/${foodId}`);
        if (alive && res.ok) {
          const detail = (await res.json()) as FoodDetail;
          setSelectedFood(detail);
          setUom(detail.defaultUoM);
          recomputeNutrition(detail.caloriesPerUnit, detail.proteinPerUnit, detail.carbsPerUnit, detail.fatPerUnit, quantity);
        }
      } catch { /* ignore */ }
    })();
    return () => { alive = false; };
  }, [isEdit, user, searchParams]);

  // Food search. Focusing the field lists the whole catalogue (empty query →
  // all foods, debounce-free); typing narrows it. Blur hides the dropdown.
  useEffect(() => {
    if (!searchFocused) return;
    const term = searchTerm.trim();
    const timer = setTimeout(async () => {
      setSearching(true);
      try {
        const params = new URLSearchParams();
        if (term) params.set('search', term);
        if (user?.id) { params.set('userId', user.id); params.set('sort', sortMode); }
        const res = await apiFetch(`/api/foods?${params.toString()}`);
        if (res.ok) setSearchResults((await res.json()) as FoodItem[]);
      } finally {
        setSearching(false);
      }
    }, term ? 200 : 0);
    return () => clearTimeout(timer);
  }, [searchTerm, sortMode, user?.id, searchFocused]);

  const selectFood = useCallback(async (food: FoodItem) => {
    try {
      const res = await apiFetch(`/api/foods/${food.id}`);
      if (!res.ok) return;
      const detail = (await res.json()) as FoodDetail;
      setSelectedFood(detail);
      setUom(detail.defaultUoM);
      setSearchTerm('');
      setSearchResults([]);
      setSearchFocused(false);
      recomputeNutrition(detail.caloriesPerUnit, detail.proteinPerUnit, detail.carbsPerUnit, detail.fatPerUnit, quantity);
    } catch { /* ignore */ }
  }, [quantity]);

  const recomputeNutrition = (calPerUnit: number, protPerUnit: number | null, carbPerUnit: number | null, fatPerUnit: number | null, qty: number) => {
    setCalories(Math.round(calPerUnit * qty));
    setProtein(protPerUnit != null ? Math.round(protPerUnit * qty * 10) / 10 : undefined);
    setCarbs(carbPerUnit != null ? Math.round(carbPerUnit * qty * 10) / 10 : undefined);
    setFat(fatPerUnit != null ? Math.round(fatPerUnit * qty * 10) / 10 : undefined);
  };

  const onQuantityChange = (qty: number) => {
    setQuantity(qty);
    if (selectedFood) {
      recomputeNutrition(selectedFood.caloriesPerUnit, selectedFood.proteinPerUnit, selectedFood.carbsPerUnit, selectedFood.fatPerUnit, qty);
    }
  };

  const submitInlineFood = async () => {
    if (!inlineName.trim()) return;
    try {
      const res = await apiFetch('/api/foods', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: inlineName, defaultUoM: inlineUoM, caloriesPerUnit: inlineCal,
          proteinPerUnit: inlineProtein, carbsPerUnit: inlineCarbs, fatPerUnit: inlineFat,
        }),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: 'Failed to create food' }));
        setError(err.error || 'Failed to create food');
        return;
      }
      const newFood = (await res.json()) as FoodDetail;
      setSelectedFood(newFood);
      setUom(newFood.defaultUoM);
      setShowInlineForm(false);
      setError(null);
      recomputeNutrition(newFood.caloriesPerUnit, newFood.proteinPerUnit, newFood.carbsPerUnit, newFood.fatPerUnit, quantity);
    } catch {
      setError('Failed to create food.');
    }
  };

  // Barcode/EAN scanning now lives in the AI entry screen's Photo mode
  // (camera / upload / scan), so the manual entry form no longer carries it.

  // Check meal-pause when meal or intake time changes
  useEffect(() => {
    if (!user) return;
    const timer = setTimeout(async () => {
      try {
        const dt = new Date(intakeAtUtc).toISOString();
        const res = await apiFetch(`/api/user/${user.id}/meal-pause-check?intakeAtUtc=${encodeURIComponent(dt)}&mealType=${encodeURIComponent(mealType)}`);
        if (res.ok) {
          const check = await res.json();
          if (check.isWithinPause) {
            setMealPauseWarning({ hoursSinceLast: check.hoursSinceLast, mealPauseHours: check.mealPauseHours });
          } else {
            setMealPauseWarning(null);
          }
        }
      } catch { setMealPauseWarning(null); }
    }, 300);
    return () => clearTimeout(timer);
  }, [mealType, intakeAtUtc, user]);

  const save = async () => {
    if (!user) return;
    if (!selectedFood) { setError('Please select a food.'); return; }
    const qty = quantity || 0;
    if (qty <= 0) { setError('Quantity must be greater than zero.'); return; }

    setSaving(true);
    setError(null);
    saveLastMealType(mealType);

    const utcDate = new Date(intakeAtUtc).toISOString();
    const body = {
      foodId: selectedFood.id,
      foodName: selectedFood.name,
      intakeAtUtc: utcDate,
      mealType,
      quantity: qty,
      uoM: uom,
      calories,
      protein,
      carbs,
      fat,
    };

    try {
      const url = isEdit
        ? `/api/user/${user.id}/entries/${entryId}`
        : `/api/user/${user.id}/entries`;
      const res = await apiFetch(url, {
        method: isEdit ? 'PUT' : 'POST',
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
  if (loading) return <div className="entry-form"><p className="settings-muted">Loading…</p></div>;

  return (
    <div className="entry-form">
      <h1>{isEdit ? 'Edit Entry' : 'Add Entry'}</h1>

      {error && <p className="form-error" role="alert">{error}</p>}

      {mealPauseWarning && (
        <p className="meal-pause-warning" role="alert">
          ⏱ Only {mealPauseWarning.hoursSinceLast}h since your last intake (pause: {mealPauseWarning.mealPauseHours}h). Just a heads-up!
        </p>
      )}

      {!isEdit && aiEnabled && !selectedFood && (
        <button
          className="ai-describe-link"
          onClick={() => navigate(`/entry/ai?meal=${encodeURIComponent(mealType)}&date=${queryDate}`)}
        >
          ✨ Describe it with AI instead
        </button>
      )}

      {/* Food search */}
      {!selectedFood ? (
        <>
          <div className="form-section">
            <label>Search foods</label>
            <div className="food-search-toolbar">
              <select
                className="search-sort"
                value={sortMode}
                onChange={(e) => setSortMode(e.target.value as SortMode)}
              >
                {(['priority', 'alphabetical', 'most-used', 'recent'] as SortMode[]).map((m) => (
                  <option key={m} value={m}>{SORT_LABELS[m]}</option>
                ))}
              </select>
            </div>
            <div className="food-search">
              <input
                className="food-search-input"
                type="text"
                placeholder="Search foods, or tap to see all…"
                value={searchTerm}
                onChange={(e) => setSearchTerm(e.target.value)}
                onFocus={() => setSearchFocused(true)}
                // Delay so a click on a result registers before the list unmounts.
                onBlur={() => setTimeout(() => setSearchFocused(false), 150)}
              />
              {searchFocused && searchResults.length > 0 && (
                <div className="search-results">
                  {searchResults.map((f) => (
                    <div key={f.id} className="search-result-item" onClick={() => selectFood(f)}>
                      <div className="search-result-name">
                        {f.name}
                        <span className="search-result-ponder" title="Priority"> {f.ponder ?? 100}</span>
                      </div>
                      <div className="search-result-detail">
                        {f.caloriesPerUnit} cal/{f.defaultUoM}
                        {f.isComposite ? ' · composite' : ''}
                        {f.usageCount != null ? ` · ${f.usageCount}×` : ''}
                      </div>
                    </div>
                  ))}
                </div>
              )}
              {searchFocused && !searching && searchResults.length === 0 && (
                <div className="search-results">
                  <div className="search-no-results">
                    {searchTerm ? 'No foods found.' : 'No foods in your catalogue yet.'}
                  </div>
                </div>
              )}
            </div>
            <button className="inline-define-link" onClick={() => setShowInlineForm(!showInlineForm)}>
              {showInlineForm ? 'Cancel' : "Can't find it? Define a new food"}
            </button>
          </div>

          {showInlineForm && (
            <div className="inline-definition">
              <h3>Define New Food</h3>
              <div className="form-section">
                <label>Name</label>
                <input type="text" value={inlineName} onChange={(e) => setInlineName(e.target.value)} />
              </div>
              <div className="entry-form-row">
                <div className="form-section">
                  <label>Default unit</label>
                  <select value={inlineUoM} onChange={(e) => setInlineUoM(e.target.value)}>
                    {UOM_OPTIONS.map((u) => <option key={u} value={u}>{u}</option>)}
                  </select>
                </div>
                <div className="form-section">
                  <label>Cal / {inlineUoM}</label>
                  <input type="number" value={inlineCal} onChange={(e) => setInlineCal(Number(e.target.value))} />
                </div>
              </div>
              <button className="add-entry-btn" onClick={submitInlineFood}>
                Create &amp; Select
              </button>
            </div>
          )}
        </>
      ) : (
        <>
          {/* Selected food */}
          <div className="form-section">
            <label>Food</label>
            <div className="selected-food">
              <div>
                <div className="selected-food-name">{selectedFood.name}</div>
                <div className="selected-food-detail">{selectedFood.caloriesPerUnit} cal/{selectedFood.defaultUoM}</div>
              </div>
              <button className="entry-row-btn" onClick={() => setSelectedFood(null)}>Change</button>
            </div>
          </div>

          {/* Quantity + UoM */}
          <div className="entry-form-row">
            <div className="form-section">
              <label>Quantity</label>
              <input type="number" min="0" step="1" value={quantity} onChange={(e) => onQuantityChange(Number(e.target.value))} />
            </div>
            <div className="form-section">
              <label>Unit</label>
              <select value={uom} onChange={(e) => setUom(e.target.value)}>
                {UOM_OPTIONS.map((u) => <option key={u} value={u}>{u}</option>)}
              </select>
            </div>
          </div>

          {/* Meal type + intake time */}
          <div className="entry-form-row">
            <div className="form-section">
              <label>Meal</label>
              <select value={mealType} onChange={(e) => setMealType(e.target.value)}>
                {MEAL_OPTIONS.map((m) => <option key={m} value={m}>{m}</option>)}
              </select>
            </div>
            <div className="form-section">
              <label>When</label>
              <input type="datetime-local" value={intakeAtUtc} onChange={(e) => setIntakeAtUtc(e.target.value)} />
            </div>
          </div>

          {/* Editable nutrition */}
          <div className="nutrition-display">
            <h3>Nutrition (editable)</h3>
            <div className="nutrition-row">
              <div className="nutrition-field">
                <label>Calories</label>
                <input type="number" value={calories} onChange={(e) => setCalories(Number(e.target.value))} />
              </div>
              <div className="nutrition-field">
                <label>Protein (g)</label>
                <input type="number" value={protein ?? ''} onChange={(e) => setProtein(e.target.value ? Number(e.target.value) : undefined)} />
              </div>
              <div className="nutrition-field">
                <label>Carbs (g)</label>
                <input type="number" value={carbs ?? ''} onChange={(e) => setCarbs(e.target.value ? Number(e.target.value) : undefined)} />
              </div>
              <div className="nutrition-field">
                <label>Fat (g)</label>
                <input type="number" value={fat ?? ''} onChange={(e) => setFat(e.target.value ? Number(e.target.value) : undefined)} />
              </div>
            </div>
          </div>

          {/* Actions */}
          <div className="form-actions">
            <button className="cancel-btn" onClick={() => navigate(-1)}>Cancel</button>
            <button className="save-btn" onClick={save} disabled={saving}>
              {saving ? 'Saving…' : 'Save Entry'}
            </button>
          </div>
        </>
      )}
    </div>
  );
}

export default EntryFormPage;
