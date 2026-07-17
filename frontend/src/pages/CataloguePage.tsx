import { useEffect, useState, useCallback, useRef } from 'react';
import { apiFetch } from '../lib/api';
import { useAuth } from '../context/AuthContext';
import { useShowMacros } from '../hooks/useShowMacros';
import { UnitSelect } from '../components/UnitSelect';
import { NumberInput } from '../components/NumberInput';
import { FoodAiAssist } from '../components/FoodAiAssist';
import { refLabel, refQty } from '../lib/units';
import '../styles/catalogue.css';

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

interface IngredientResponse {
  childFoodId: string;
  childFoodName: string;
  quantity: number;
  uoM: string;
}

interface FoodDetail extends FoodItem {
  proteinPerUnit: number | null;
  carbsPerUnit: number | null;
  fatPerUnit: number | null;
  ingredients: IngredientResponse[];
}

export interface FoodFormData {
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
  proteinPerUnit: number | undefined;
  carbsPerUnit: number | undefined;
  fatPerUnit: number | undefined;
}

interface IngredientFormItem {
  childFoodId: string;
  childFoodName: string;
  quantity: number;
  uoM: string;
  isInline: boolean;
  inlineName: string;
  inlineUoM: string;
  inlineCal: number;
}

const emptyForm: FoodFormData = {
  name: '', defaultUoM: 'g', caloriesPerUnit: 0,
  proteinPerUnit: undefined, carbsPerUnit: undefined, fatPerUnit: undefined,
};


function CataloguePage() {
  const { user } = useAuth();
  const showMacros = useShowMacros();
  const [foods, setFoods] = useState<FoodItem[]>([]);
  const [search, setSearch] = useState('');
  const [sortMode, setSortMode] = useState<SortMode>('priority');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FoodFormData>(emptyForm);
  const [ingredients, setIngredients] = useState<IngredientFormItem[]>([]);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const [duplicateFoodId, setDuplicateFoodId] = useState<string | null>(null);

  // Ingredient search state
  const [ingSearchTerm, setIngSearchTerm] = useState('');
  const [ingSearchResults, setIngSearchResults] = useState<FoodItem[]>([]);
  const [addingIngToIndex, setAddingIngToIndex] = useState<number | null>(null);

  const dialogRef = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (showForm && !dialog.open) {
      dialog.showModal();
    } else if (!showForm && dialog.open) {
      dialog.close();
    }
  }, [showForm]);

  // Composite-food ingredients (lazy-loaded once, then cached). Shown two ways:
  // a hover popover (desktop) and a click-to-expand caret that lists them inline
  // underneath the row (works on touch). Both are composite-only.
  const [openIngredients, setOpenIngredients] = useState<string | null>(null);
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set());
  const [ingCache, setIngCache] = useState<Record<string, IngredientResponse[]>>({});

  const loadIngredients = useCallback(async (f: FoodItem) => {
    if (!f.isComposite || ingCache[f.id]) return;
    try {
      const res = await apiFetch(`/api/foods/${f.id}`);
      if (res.ok) {
        const d = (await res.json()) as FoodDetail;
        setIngCache((c) => ({ ...c, [f.id]: d.ingredients }));
      }
    } catch { /* ignore — preview is best-effort */ }
  }, [ingCache]);

  const previewIngredients = useCallback((f: FoodItem) => {
    if (!f.isComposite) return;
    setOpenIngredients(f.id);
    loadIngredients(f);
  }, [loadIngredients]);

  const toggleExpand = useCallback((f: FoodItem) => {
    setExpandedIds((prev) => {
      const next = new Set(prev);
      if (next.has(f.id)) next.delete(f.id); else next.add(f.id);
      return next;
    });
    loadIngredients(f);
  }, [loadIngredients]);

  const fetchFoods = useCallback(async () => {
    setLoading(true);
    try {
      const params = new URLSearchParams();
      if (search) params.set('search', search);
      if (user?.id) { params.set('userId', user.id); params.set('sort', sortMode); }
      const qs = params.toString();
      const url = qs ? `/api/foods?${qs}` : '/api/foods';
      const res = await apiFetch(url);
      if (res.ok) setFoods((await res.json()) as FoodItem[]);
    } catch {
      setError("Couldn't load foods.");
    } finally {
      setLoading(false);
    }
  }, [search, sortMode, user?.id]);

  useEffect(() => { fetchFoods(); }, [fetchFoods]);

  const startAdd = () => {
    setEditingId(null);
    setForm(emptyForm);
    setIngredients([]);
    setFormError(null);
    setDuplicateFoodId(null);
    setShowForm(true);
  };

  const startEdit = async (id: string) => {
    try {
      const res = await apiFetch(`/api/foods/${id}`);
      if (!res.ok) return;
      const f = (await res.json()) as FoodDetail;
      setEditingId(id);
      const rq = refQty(f.defaultUoM);
      setForm({
        name: f.name, defaultUoM: f.defaultUoM,
        caloriesPerUnit: Math.round(f.caloriesPerUnit * rq * 100) / 100,
        proteinPerUnit: f.proteinPerUnit != null ? Math.round(f.proteinPerUnit * rq * 100) / 100 : undefined,
        carbsPerUnit: f.carbsPerUnit != null ? Math.round(f.carbsPerUnit * rq * 100) / 100 : undefined,
        fatPerUnit: f.fatPerUnit != null ? Math.round(f.fatPerUnit * rq * 100) / 100 : undefined,
      });
      setIngredients(f.ingredients.map((i) => ({
        childFoodId: i.childFoodId,
        childFoodName: i.childFoodName,
        quantity: i.quantity,
        uoM: i.uoM,
        isInline: false,
        inlineName: '', inlineUoM: 'g', inlineCal: 0,
      })));
      setFormError(null);
      setDuplicateFoodId(null);
      setShowForm(true);
    } catch { /* ignore */ }
  };

  const cancelForm = () => {
    if (!showForm) return; // guard against double-invoke from dialog onClose
    setShowForm(false);
    setEditingId(null);
    setDuplicateFoodId(null);
  };

  const addIngredient = (existing: FoodItem) => {
    setIngredients((prev) => [...prev, {
      childFoodId: existing.id,
      childFoodName: existing.name,
      quantity: 0,
      uoM: existing.defaultUoM,
      isInline: false,
      inlineName: '', inlineUoM: 'g', inlineCal: 0,
    }]);
    setIngSearchTerm('');
    setIngSearchResults([]);
    setAddingIngToIndex(null);
  };

  const addInlineIngredient = () => {
    setIngredients((prev) => [...prev, {
      childFoodId: '', childFoodName: '',
      quantity: 0, uoM: 'g',
      isInline: true,
      inlineName: '', inlineUoM: 'g', inlineCal: 0,
    }]);
    setAddingIngToIndex(null);
  };

  const removeIngredient = (index: number) => {
    setIngredients((prev) => prev.filter((_, i) => i !== index));
  };

  const updateIngredient = (index: number, updates: Partial<IngredientFormItem>) => {
    setIngredients((prev) => prev.map((ing, i) => i === index ? { ...ing, ...updates } : ing));
  };

  // Debounced ingredient search
  useEffect(() => {
    if (!ingSearchTerm.trim()) { setIngSearchResults([]); return; }
    const timer = setTimeout(async () => {
      try {
        const params = new URLSearchParams({ search: ingSearchTerm });
        if (user?.id) { params.set('userId', user.id); params.set('sort', sortMode); }
        const res = await apiFetch(`/api/foods?${params.toString()}`);
        if (res.ok) setIngSearchResults((await res.json()) as FoodItem[]);
      } catch { /* ignore */ }
    }, 200);
    return () => clearTimeout(timer);
  }, [ingSearchTerm, sortMode, user?.id]);

  const handleApply = (prefill: Partial<FoodFormData> & { matchedFoodId: string | null }) => {
    setForm((f) => ({ ...f, ...prefill }));
    if (prefill.matchedFoodId && editingId === null) {
      setDuplicateFoodId(prefill.matchedFoodId);
    }
  };

  const saveFood = async () => {
    if (!form.name.trim()) { setFormError('Name is required.'); return; }

    setSaving(true);
    setFormError(null);
    setDuplicateFoodId(null);

    const ingredientRequests = ingredients.map((ing) => {
      if (ing.isInline) {
        return {
          inlineChild: {
            name: ing.inlineName,
            defaultUoM: ing.inlineUoM,
            caloriesPerUnit: ing.inlineCal,
          },
          quantity: ing.quantity,
          uoM: ing.uoM,
        };
      }
      return {
        childFoodId: ing.childFoodId,
        quantity: ing.quantity,
        uoM: ing.uoM,
      };
    });

    const rq = refQty(form.defaultUoM);
    const body = {
      ...form,
      caloriesPerUnit: form.caloriesPerUnit / rq,
      proteinPerUnit: form.proteinPerUnit != null ? form.proteinPerUnit / rq : null,
      carbsPerUnit: form.carbsPerUnit != null ? form.carbsPerUnit / rq : null,
      fatPerUnit: form.fatPerUnit != null ? form.fatPerUnit / rq : null,
      ingredients: ingredientRequests,
    };

    try {
      const url = editingId ? `/api/foods/${editingId}` : '/api/foods';
      const method = editingId ? 'PUT' : 'POST';
      const res = await apiFetch(url, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: 'Save failed' }));
        if (res.status === 409 && err.existingFoodId) {
          setDuplicateFoodId(err.existingFoodId);
          setFormError(err.error || 'A food with this name already exists.');
          return;
        }
        throw new Error(err.error || 'Save failed');
      }
      setShowForm(false);
      setEditingId(null);
      fetchFoods();
    } catch (e) {
      setFormError(e instanceof Error ? e.message : 'Save failed.');
    } finally {
      setSaving(false);
    }
  };

  const setPonder = async (foodId: string, ponder: number) => {
    if (!user?.id) return;
    try {
      const res = await apiFetch(`/api/foods/${foodId}/priority?userId=${encodeURIComponent(user.id)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ponder }),
      });
      if (res.ok) fetchFoods();
    } catch { /* ignore */ }
  };

  const deleteFood = async (id: string) => {
    if (!confirm('Delete this food? Any entries that used it will keep their snapshotted values.')) return;
    try {
      const res = await apiFetch(`/api/foods/${id}`, { method: 'DELETE' });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: 'Delete failed' }));
        setError(err.error || 'Delete failed');
        return;
      }
      fetchFoods();
    } catch {
      setError('Delete failed.');
    }
  };

  if (!user) return null;

  return (
    <div className="catalogue-page">
      <h1>Food Catalogue</h1>

      {error && <p className="form-error" role="alert">{error}</p>}

      <div className="catalogue-toolbar">
        <input
          className="catalogue-search"
          type="text"
          placeholder="Search foods…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select
          className="catalogue-sort"
          value={sortMode}
          onChange={(e) => setSortMode(e.target.value as SortMode)}
        >
          {(['priority', 'alphabetical', 'most-used', 'recent'] as SortMode[]).map((m) => (
            <option key={m} value={m}>{SORT_LABELS[m]}</option>
          ))}
        </select>
        <button className="catalogue-add-btn" onClick={startAdd} aria-label="Add food" title="Add food">
          <svg viewBox="0 0 24 24"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>
        </button>
      </div>

      {/* Food form dialog */}
      <dialog ref={dialogRef} className="food-form-dialog" onClose={cancelForm}>
        <div className="food-form">
          <h2>{editingId ? 'Edit Food' : 'Add Food'}</h2>
          {formError && <p className="form-error" role="alert">{formError}</p>}
          {duplicateFoodId && (
            <p className="form-duplicate-hint">
              <button type="button" className="link-button" onClick={() => startEdit(duplicateFoodId)}>
                Edit the existing food instead
              </button>
            </p>
          )}

          {/* AI assist only when DEFINING a new food — editing an existing one is a
              manual correction, not an estimation task. */}
          {editingId === null && <FoodAiAssist userId={user.id} onApply={handleApply} />}

          <div className="food-form-section">
            <label>Name</label>
            <input
              type="text"
              value={form.name}
              onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
              placeholder="e.g. Chicken Breast"
            />
          </div>

          <div className="food-form-row">
            <div className="food-form-section">
              <label>Default unit</label>
              <UnitSelect value={form.defaultUoM} onChange={(v) => setForm((f) => ({ ...f, defaultUoM: v }))} />
            </div>
            <div className="food-form-section">
              <label>Calories {refLabel(form.defaultUoM)}</label>
              <NumberInput
                min="0" step="0.1"
                value={form.caloriesPerUnit}
                onValueChange={(v) => setForm((f) => ({ ...f, caloriesPerUnit: v ?? 0 }))}
              />
            </div>
          </div>

          {showMacros && (
            <>
              <div className="food-form-row">
                <div className="food-form-section">
                  <label>Protein {refLabel(form.defaultUoM)}</label>
                  <input
                    type="number" min="0" step="0.1"
                    value={form.proteinPerUnit ?? ''}
                    onChange={(e) => setForm((f) => ({ ...f, proteinPerUnit: e.target.value ? Number(e.target.value) : undefined }))}
                  />
                </div>
                <div className="food-form-section">
                  <label>Carbs {refLabel(form.defaultUoM)}</label>
                  <input
                    type="number" min="0" step="0.1"
                    value={form.carbsPerUnit ?? ''}
                    onChange={(e) => setForm((f) => ({ ...f, carbsPerUnit: e.target.value ? Number(e.target.value) : undefined }))}
                  />
                </div>
              </div>

              <div className="food-form-section">
                <label>Fat {refLabel(form.defaultUoM)}</label>
                <input
                  type="number" min="0" step="0.1"
                  value={form.fatPerUnit ?? ''}
                  onChange={(e) => setForm((f) => ({ ...f, fatPerUnit: e.target.value ? Number(e.target.value) : undefined }))}
                  style={{ maxWidth: '200px' }}
                />
              </div>
            </>
          )}

          {/* Ingredients */}
          <div className="ingredients-section">
            <h3>Ingredients</h3>
            {ingredients.map((ing, i) => (
              <div key={i}>
                <div className="ingredient-row">
                  {ing.isInline ? (
                    <>
                      <input
                        className="inline-ingredient-name"
                        placeholder="Name"
                        value={ing.inlineName}
                        onChange={(e) => updateIngredient(i, { inlineName: e.target.value, childFoodName: e.target.value })}
                      />
                      <UnitSelect value={ing.inlineUoM} onChange={(v) => updateIngredient(i, { inlineUoM: v })} />
                      <NumberInput
                        className="inline-ingredient-cal"
                        min="0" step="0.1"
                        placeholder="Cal/unit"
                        value={ing.inlineCal}
                        onValueChange={(v) => updateIngredient(i, { inlineCal: v ?? 0 })}
                      />
                    </>
                  ) : (
                    <span className="ingredient-name">{ing.childFoodName}</span>
                  )}
                  <NumberInput
                    className="ingredient-qty"
                    min="0" step="0.1"
                    value={ing.quantity}
                    onValueChange={(v) => updateIngredient(i, { quantity: v ?? 0 })}
                  />
                  {ing.isInline ? (
                    <UnitSelect className="ingredient-uom" value={ing.uoM} onChange={(v) => updateIngredient(i, { uoM: v })} />
                  ) : (
                    /* Same rule as logging in the diary: the unit lives in the food's
                       definition — change it there, not per ingredient. */
                    <div className="unit-fixed ingredient-uom" title="Set by the food's definition — change it in the catalogue.">
                      {ing.uoM}
                    </div>
                  )}
                  <button className="ingredient-remove" onClick={() => removeIngredient(i)}>×</button>
                </div>
              </div>
            ))}

            {addingIngToIndex !== null ? (
              <div style={{ position: 'relative' }}>
                <input
                  type="text"
                  placeholder="Search ingredient…"
                  value={ingSearchTerm}
                  onChange={(e) => setIngSearchTerm(e.target.value)}
                  style={{ width: '100%', marginBottom: '0.25rem' }}
                />
                {ingSearchResults.length > 0 && (
                  <div className="search-results">
                    {ingSearchResults.map((f) => (
                      <div key={f.id} className="search-result-item" onClick={() => addIngredient(f)}>
                        <div className="search-result-name">{f.name}</div>
                        <div className="search-result-detail">
                          {Math.round(f.caloriesPerUnit * refQty(f.defaultUoM) * 10) / 10} cal/{refQty(f.defaultUoM)} {f.defaultUoM}
                        </div>
                      </div>
                    ))}
                  </div>
                )}
                <button className="inline-define-link" onClick={addInlineIngredient}>
                  Or define a new ingredient
                </button>
              </div>
            ) : (
              <button className="add-ingredient-btn" onClick={() => setAddingIngToIndex(-1)}>
                + Add Ingredient
              </button>
            )}
          </div>

          <div className="food-form-actions">
            <button className="food-form-cancel" onClick={cancelForm}>Cancel</button>
            <button className="food-form-save" onClick={saveFood} disabled={saving}>
              {saving ? 'Saving…' : 'Save Food'}
            </button>
          </div>
        </div>
      </dialog>

      {/* Food list */}
      {loading ? (
        <p className="settings-muted" style={{ textAlign: 'center' }}>Loading foods…</p>
      ) : (
        <ul className="food-list">
          {foods.map((f) => (
            <li key={f.id}>
              <div
                className="food-card"
                onMouseEnter={() => previewIngredients(f)}
                onMouseLeave={() => setOpenIngredients(null)}
              >
                <div className="food-card-main" onClick={() => startEdit(f.id)}>
                  <div className="food-card-name">{f.name}</div>
                  <div className="food-card-detail">
                    {Math.round(f.caloriesPerUnit * refQty(f.defaultUoM) * 10) / 10} cal/{refQty(f.defaultUoM)} {f.defaultUoM}
                    {f.isComposite ? ` · ${f.ingredientCount} ingredient${f.ingredientCount !== 1 ? 's' : ''}` : ''}
                    {f.usageCount != null ? ` · ${f.usageCount}×` : ''}
                  </div>
                </div>
                {f.isComposite && (
                  <button
                    className="food-expander"
                    aria-expanded={expandedIds.has(f.id)}
                    aria-label={`${expandedIds.has(f.id) ? 'Hide' : 'Show'} ingredients of ${f.name}`}
                    onClick={(e) => { e.stopPropagation(); toggleExpand(f); }}
                  >
                    <span className={`food-expander-caret${expandedIds.has(f.id) ? ' open' : ''}`}>▾</span>
                  </button>
                )}
                {f.isComposite && <span className="food-card-badge">composite</span>}
                <div
                  className="food-ponder"
                  onClick={(e) => e.stopPropagation()}
                  title="Priority — lower shows sooner in lists"
                >
                  <button
                    className="ponder-btn"
                    aria-label={`Lower priority of ${f.name}`}
                    onClick={() => setPonder(f.id, Math.max(0, (f.ponder ?? 100) - 10))}
                  >−</button>
                  <span className="ponder-value">{f.ponder ?? 100}</span>
                  <button
                    className="ponder-btn"
                    aria-label={`Raise priority of ${f.name}`}
                    onClick={() => setPonder(f.id, (f.ponder ?? 100) + 10)}
                  >+</button>
                </div>
                <button
                  className="food-card-edit"
                  onClick={(e) => { e.stopPropagation(); startEdit(f.id); }}
                  aria-label={`Edit ${f.name}`}
                  title="Edit"
                >✎</button>
                <button
                  className="food-card-delete"
                  onClick={(e) => { e.stopPropagation(); deleteFood(f.id); }}
                  aria-label={`Delete ${f.name}`}
                  title="Delete"
                >✕</button>

                {f.isComposite && openIngredients === f.id && !expandedIds.has(f.id) && (
                  <div className="food-ingredients-pop" role="tooltip">
                    <div className="food-ingredients-title">Ingredients</div>
                    {ingCache[f.id] ? (
                      ingCache[f.id].length > 0 ? (
                        <ul className="food-ingredients-list">
                          {ingCache[f.id].map((ing) => (
                            <li key={ing.childFoodId}>
                              <span>{ing.childFoodName}</span>
                              <span className="food-ingredients-qty">{ing.quantity} {ing.uoM}</span>
                            </li>
                          ))}
                        </ul>
                      ) : (
                        <div className="settings-muted">No ingredients listed.</div>
                      )
                    ) : (
                      <div className="settings-muted">Loading…</div>
                    )}
                  </div>
                )}

                {f.isComposite && expandedIds.has(f.id) && (
                  <div className="food-ingredients-inline">
                    <div className="food-ingredients-title">Ingredients</div>
                    {ingCache[f.id] ? (
                      ingCache[f.id].length > 0 ? (
                        <ul className="food-ingredients-list">
                          {ingCache[f.id].map((ing) => (
                            <li key={ing.childFoodId}>
                              <span>{ing.childFoodName}</span>
                              <span className="food-ingredients-qty">{ing.quantity} {ing.uoM}</span>
                            </li>
                          ))}
                        </ul>
                      ) : (
                        <div className="settings-muted">No ingredients listed.</div>
                      )
                    ) : (
                      <div className="settings-muted">Loading…</div>
                    )}
                  </div>
                )}
              </div>
            </li>
          ))}
          {foods.length === 0 && !loading && (
            <p className="settings-muted" style={{ textAlign: 'center' }}>No foods in the catalogue yet.</p>
          )}
        </ul>
      )}
    </div>
  );
}

export default CataloguePage;
