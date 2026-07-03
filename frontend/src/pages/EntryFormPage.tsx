import { useEffect, useState, useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { useNavigate, useSearchParams, useParams } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import { getLastMealType, saveLastMealType, getFoodSortMode } from '../lib/storage';
import { type CatalogueFood } from '../lib/foods';
import { useShowMacros } from '../hooks/useShowMacros';
import { UnitSelect } from '../components/UnitSelect';
import { NumberInput } from '../components/NumberInput';
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

const MEAL_OPTIONS = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];

function toLocalDatetime(utc: string): string {
  const d = new Date(utc);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// HH:MM in the viewer's local zone — used for the meal-order conflict time.
function formatLocalTime(utc: string): string {
  const d = new Date(utc);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function EntryFormPage() {
  const { user } = useAuth();
  const showMacros = useShowMacros();
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
  // Sort order is a Settings preference now (read once per visit), not an on-screen control.
  const sortMode = getFoodSortMode();
  const [selectedFood, setSelectedFood] = useState<FoodDetail | null>(null);
  const [showInlineForm, setShowInlineForm] = useState(false);

  // A catalogue food whose name (case-insensitively) matches the one being defined.
  const [inlineDup, setInlineDup] = useState<CatalogueFood | null>(null);

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
  const [mealPauseWarning, setMealPauseWarning] = useState<{ hoursSinceLast: number; mealPauseHours: number; lastFoodName: string | null; lastMealType: string | null } | null>(null);
  const [mealOrderWarning, setMealOrderWarning] = useState<string | null>(null);

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

  // Live duplicate-name check for the inline-define form. Queried per keystroke
  // (debounced) rather than from a one-time snapshot, so a food you created moments
  // ago is still caught — and the match is case-insensitive.
  useEffect(() => {
    const name = inlineName.trim();
    if (!showInlineForm || !name) { setInlineDup(null); return; }
    const timer = setTimeout(async () => {
      try {
        const res = await apiFetch(`/api/foods?search=${encodeURIComponent(name)}`);
        if (!res.ok) return;
        const foods = (await res.json()) as CatalogueFood[];
        setInlineDup(foods.find((f) => f.name.trim().toLowerCase() === name.toLowerCase()) ?? null);
      } catch { /* leave the warning as-is */ }
    }, 300);
    return () => clearTimeout(timer);
  }, [inlineName, showInlineForm]);

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
        // Minutes to add to UTC to get the user's local wall-clock (e.g. +120 for UTC+2), so the
        // server scopes the meal-order check to the user's LOCAL day — otherwise a late dinner and the
        // next morning's breakfast can share a UTC date and trip a false "breakfast after dinner".
        const tzOffsetMinutes = -new Date(intakeAtUtc).getTimezoneOffset();
        const res = await apiFetch(`/api/user/${user.id}/meal-pause-check?intakeAtUtc=${encodeURIComponent(dt)}&mealType=${encodeURIComponent(mealType)}&tzOffsetMinutes=${tzOffsetMinutes}`);
        if (res.ok) {
          const check = await res.json();
          if (check.isWithinPause) {
            setMealPauseWarning({ hoursSinceLast: check.hoursSinceLast, mealPauseHours: check.mealPauseHours, lastFoodName: check.lastFoodName ?? null, lastMealType: check.lastMealType ?? null });
          } else {
            setMealPauseWarning(null);
          }
          // The server sends the conflict time as a raw UTC instant; format it in the
          // viewer's local zone here (the server can't know the user's timezone).
          if (check.mealOrderWarning) {
            const at = check.mealOrderConflictAtUtc
              ? ` (finished at ${formatLocalTime(check.mealOrderConflictAtUtc)})`
              : '';
            setMealOrderWarning(`${check.mealOrderWarning}${at}.`);
          } else {
            setMealOrderWarning(null);
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

  const useExistingFood = (food: CatalogueFood) => {
    setShowInlineForm(false);
    setError(null);
    selectFood({ id: food.id } as FoodItem);
  };

  if (!user) return null;
  if (loading) return <div className="entry-form"><p className="settings-muted">Loading…</p></div>;

  return (
    <div className="entry-form">
      <h1>{isEdit ? 'Edit Entry' : 'Add Entry'}</h1>

      {error && <p className="form-error" role="alert">{error}</p>}

      {mealPauseWarning && (
        <p className="meal-pause-warning" role="alert">
          ⏱ Only {mealPauseWarning.hoursSinceLast}h since {mealPauseWarning.lastFoodName ? `"${mealPauseWarning.lastFoodName}"` : 'your last intake'}{mealPauseWarning.lastMealType ? ` / ${mealPauseWarning.lastMealType}` : ''} (pause: {mealPauseWarning.mealPauseHours}h). Just a heads-up!
        </p>
      )}

      {mealOrderWarning && !mealPauseWarning && (
        <p className="meal-pause-warning" role="alert">
          ⚠ {mealOrderWarning}
        </p>
      )}

      {/* Food search */}
      {!selectedFood ? (
        <>
          <div className="form-section">
            <label>Search foods</label>
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
              {showInlineForm ? 'Cancel' : "Can't find it? Add it to the catalogue"}
            </button>
          </div>

          {showInlineForm && (
            <div className="inline-definition">
              <h3>Define New Food</h3>
              <div className="form-section">
                <label>Name</label>
                <input type="text" value={inlineName} onChange={(e) => setInlineName(e.target.value)} />
                {inlineDup && (
                  <p className="dup-food-warning">
                    ⚠ "{inlineDup.name}" is already in your catalogue.{' '}
                    <button type="button" className="dup-food-use" onClick={() => useExistingFood(inlineDup)}>
                      Use the existing one
                    </button>{' '}
                    instead of creating a duplicate.
                  </p>
                )}
              </div>
              <div className="entry-form-row">
                <div className="form-section">
                  <label>Default unit</label>
                  <UnitSelect value={inlineUoM} onChange={setInlineUoM} />
                </div>
                <div className="form-section">
                  <label>Cal / {inlineUoM}</label>
                  <NumberInput value={inlineCal} onValueChange={(v) => setInlineCal(v ?? 0)} />
                </div>
              </div>
              <button className="add-entry-btn" onClick={submitInlineFood}>
                Create &amp; Select
              </button>
            </div>
          )}

          {!isEdit && aiEnabled && (
            <div className="ai-entry-cta-row">
              <button
                className="ai-describe-link"
                onClick={() => navigate(`/entry/ai?meal=${encodeURIComponent(mealType)}&date=${queryDate}`)}
              >
                ✨ Use AI Instead
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
              <NumberInput min="0" step="1" value={quantity} onValueChange={(v) => onQuantityChange(v ?? 0)} />
            </div>
            <div className="form-section">
              <label>Unit</label>
              {/* The unit is fixed by the food's definition — to change it, edit the food in
                  the catalogue (the AI flow stays freely editable, since it can be wrong). */}
              <div className="unit-fixed" title="Set by the food's definition — change it in the catalogue.">
                {uom}
              </div>
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
                <NumberInput value={calories} onValueChange={(v) => setCalories(v ?? 0)} />
              </div>
              {showMacros && (
                <>
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
                </>
              )}
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
