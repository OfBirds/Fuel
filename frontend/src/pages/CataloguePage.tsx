import { useEffect, useState, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';
import '../styles/catalogue.css';

interface FoodItem {
  id: string;
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
  ingredientCount: number;
  isComposite: boolean;
}

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

interface FoodFormData {
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

const UOM_OPTIONS = ['g', 'ml', 'piece'];

function CataloguePage() {
  const { user } = useAuth();
  const [foods, setFoods] = useState<FoodItem[]>([]);
  const [search, setSearch] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Form state
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [form, setForm] = useState<FoodFormData>(emptyForm);
  const [ingredients, setIngredients] = useState<IngredientFormItem[]>([]);
  const [saving, setSaving] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);

  // Ingredient search state
  const [ingSearchTerm, setIngSearchTerm] = useState('');
  const [ingSearchResults, setIngSearchResults] = useState<FoodItem[]>([]);
  const [addingIngToIndex, setAddingIngToIndex] = useState<number | null>(null);

  const fetchFoods = useCallback(async () => {
    setLoading(true);
    try {
      const url = search ? `/api/foods?search=${encodeURIComponent(search)}` : '/api/foods';
      const res = await fetch(url);
      if (res.ok) setFoods((await res.json()) as FoodItem[]);
    } catch {
      setError("Couldn't load foods.");
    } finally {
      setLoading(false);
    }
  }, [search]);

  useEffect(() => { fetchFoods(); }, [fetchFoods]);

  const startAdd = () => {
    setEditingId(null);
    setForm(emptyForm);
    setIngredients([]);
    setFormError(null);
    setShowForm(true);
  };

  const startEdit = async (id: string) => {
    try {
      const res = await fetch(`/api/foods/${id}`);
      if (!res.ok) return;
      const f = (await res.json()) as FoodDetail;
      setEditingId(id);
      setForm({
        name: f.name, defaultUoM: f.defaultUoM, caloriesPerUnit: f.caloriesPerUnit,
        proteinPerUnit: f.proteinPerUnit ?? undefined,
        carbsPerUnit: f.carbsPerUnit ?? undefined,
        fatPerUnit: f.fatPerUnit ?? undefined,
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
      setShowForm(true);
    } catch { /* ignore */ }
  };

  const cancelForm = () => {
    setShowForm(false);
    setEditingId(null);
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
        const res = await fetch(`/api/foods?search=${encodeURIComponent(ingSearchTerm)}`);
        if (res.ok) setIngSearchResults((await res.json()) as FoodItem[]);
      } catch { /* ignore */ }
    }, 200);
    return () => clearTimeout(timer);
  }, [ingSearchTerm]);

  const saveFood = async () => {
    if (!form.name.trim()) { setFormError('Name is required.'); return; }

    setSaving(true);
    setFormError(null);

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

    const body = {
      ...form,
      proteinPerUnit: form.proteinPerUnit ?? null,
      carbsPerUnit: form.carbsPerUnit ?? null,
      fatPerUnit: form.fatPerUnit ?? null,
      ingredients: ingredientRequests,
    };

    try {
      const url = editingId ? `/api/foods/${editingId}` : '/api/foods';
      const method = editingId ? 'PUT' : 'POST';
      const res = await fetch(url, {
        method,
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      });
      if (!res.ok) {
        const err = await res.json().catch(() => ({ error: 'Save failed' }));
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

  const deleteFood = async (id: string) => {
    if (!confirm('Delete this food? Any entries that used it will keep their snapshotted values.')) return;
    try {
      const res = await fetch(`/api/foods/${id}`, { method: 'DELETE' });
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
        <button className="catalogue-add-btn" onClick={startAdd}>+ Add Food</button>
      </div>

      {/* Food form */}
      {showForm && (
        <div className="food-form">
          <h2>{editingId ? 'Edit Food' : 'Add Food'}</h2>
          {formError && <p className="form-error" role="alert">{formError}</p>}

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
              <label>Default UoM</label>
              <select value={form.defaultUoM} onChange={(e) => setForm((f) => ({ ...f, defaultUoM: e.target.value }))}>
                {UOM_OPTIONS.map((u) => <option key={u} value={u}>{u}</option>)}
              </select>
            </div>
            <div className="food-form-section">
              <label>Calories per {form.defaultUoM}</label>
              <input
                type="number" min="0" step="0.1"
                value={form.caloriesPerUnit}
                onChange={(e) => setForm((f) => ({ ...f, caloriesPerUnit: Number(e.target.value) }))}
              />
            </div>
          </div>

          <div className="food-form-row">
            <div className="food-form-section">
              <label>Protein / {form.defaultUoM} (g)</label>
              <input
                type="number" min="0" step="0.1"
                value={form.proteinPerUnit ?? ''}
                onChange={(e) => setForm((f) => ({ ...f, proteinPerUnit: e.target.value ? Number(e.target.value) : undefined }))}
              />
            </div>
            <div className="food-form-section">
              <label>Carbs / {form.defaultUoM} (g)</label>
              <input
                type="number" min="0" step="0.1"
                value={form.carbsPerUnit ?? ''}
                onChange={(e) => setForm((f) => ({ ...f, carbsPerUnit: e.target.value ? Number(e.target.value) : undefined }))}
              />
            </div>
          </div>

          <div className="food-form-section">
            <label>Fat / {form.defaultUoM} (g)</label>
            <input
              type="number" min="0" step="0.1"
              value={form.fatPerUnit ?? ''}
              onChange={(e) => setForm((f) => ({ ...f, fatPerUnit: e.target.value ? Number(e.target.value) : undefined }))}
              style={{ maxWidth: '200px' }}
            />
          </div>

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
                      <select value={ing.inlineUoM} onChange={(e) => updateIngredient(i, { inlineUoM: e.target.value })}>
                        {UOM_OPTIONS.map((u) => <option key={u} value={u}>{u}</option>)}
                      </select>
                      <input
                        className="inline-ingredient-cal"
                        type="number" min="0" step="0.1"
                        placeholder="Cal/unit"
                        value={ing.inlineCal || ''}
                        onChange={(e) => updateIngredient(i, { inlineCal: Number(e.target.value) })}
                      />
                    </>
                  ) : (
                    <span className="ingredient-name">{ing.childFoodName}</span>
                  )}
                  <input
                    className="ingredient-qty"
                    type="number" min="0" step="0.1"
                    value={ing.quantity || ''}
                    onChange={(e) => updateIngredient(i, { quantity: Number(e.target.value) })}
                  />
                  <select className="ingredient-uom" value={ing.uoM} onChange={(e) => updateIngredient(i, { uoM: e.target.value })}>
                    {UOM_OPTIONS.map((u) => <option key={u} value={u}>{u}</option>)}
                  </select>
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
                        <div className="search-result-detail">{f.caloriesPerUnit} cal/{f.defaultUoM}</div>
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
      )}

      {/* Food list */}
      {loading ? (
        <p className="settings-muted" style={{ textAlign: 'center' }}>Loading foods…</p>
      ) : (
        <ul className="food-list">
          {foods.map((f) => (
            <li key={f.id}>
              <div className="food-card" onClick={() => startEdit(f.id)}>
                <div className="food-card-main">
                  <div className="food-card-name">{f.name}</div>
                  <div className="food-card-detail">
                    {f.caloriesPerUnit} cal/{f.defaultUoM}
                    {f.isComposite ? ` · ${f.ingredientCount} ingredient${f.ingredientCount !== 1 ? 's' : ''}` : ''}
                  </div>
                </div>
                {f.isComposite && <span className="food-card-badge">composite</span>}
                <button
                  className="food-card-delete"
                  onClick={(e) => { e.stopPropagation(); deleteFood(f.id); }}
                >Delete</button>
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
