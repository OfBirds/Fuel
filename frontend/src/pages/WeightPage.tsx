import { useEffect, useState, useCallback } from 'react';
import { useAuth } from '../context/AuthContext';
import '../styles/weight.css';

interface WeightEntry {
  id: string;
  weight: number;
  recordedAtUtc: string;
  deltaPercent: number | null;
}

function formatDate(utc: string): string {
  return new Date(utc).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
}

function toLocalDate(utc: string): string {
  const d = new Date(utc);
  return d.toISOString().slice(0, 10);
}

function WeightPage() {
  const { user } = useAuth();
  const [weights, setWeights] = useState<WeightEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Add form
  const [adding, setAdding] = useState(false);
  const [newWeight, setNewWeight] = useState('');
  const [newDate, setNewDate] = useState(() => new Date().toISOString().slice(0, 10));
  const [saving, setSaving] = useState(false);

  const fetchWeights = useCallback(async () => {
    if (!user) return;
    setLoading(true);
    try {
      const res = await fetch(`/api/user/${user.id}/weights`);
      if (res.ok) setWeights((await res.json()) as WeightEntry[]);
    } catch {
      setError("Couldn't load weight history.");
    } finally {
      setLoading(false);
    }
  }, [user]);

  useEffect(() => { fetchWeights(); }, [fetchWeights]);

  const addWeight = async () => {
    if (!user) return;
    const w = Number(newWeight);
    if (!w || w <= 0) return;
    setSaving(true);
    setError(null);
    try {
      const res = await fetch(`/api/user/${user.id}/weights`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          weight: w,
          recordedAtUtc: new Date(newDate).toISOString(),
        }),
      });
      if (!res.ok) throw new Error('Save failed');
      setAdding(false);
      setNewWeight('');
      fetchWeights();
    } catch {
      setError('Failed to save weigh-in.');
    } finally {
      setSaving(false);
    }
  };

  const deleteWeight = async (id: string) => {
    if (!user || !confirm('Delete this weigh-in?')) return;
    const previous = weights;
    setWeights((prev) => prev.filter((w) => w.id !== id));
    try {
      const res = await fetch(`/api/user/${user.id}/weights/${id}`, { method: 'DELETE' });
      if (!res.ok) throw new Error('Delete failed');
    } catch {
      setWeights(previous);
      setError('Failed to delete weigh-in.');
    }
  };

  const renderDelta = (d: number | null) => {
    if (d === null || d === undefined) return <span className="weight-delta neutral">—</span>;
    if (Math.abs(d) < 0.05) return <span className="weight-delta neutral">→ 0%</span>;
    const cls = d < 0 ? 'negative' : 'positive';
    const arrow = d < 0 ? '↓' : '↑';
    return <span className={`weight-delta ${cls}`}>{arrow} {Math.abs(d).toFixed(1)}%</span>;
  };

  if (!user) return null;

  return (
    <div className="weight-page">
      <h1>Weight Register</h1>

      {error && <p className="form-error" role="alert">{error}</p>}

      {!adding ? (
        <button className="weight-add-btn" onClick={() => setAdding(true)} style={{ marginBottom: '1rem' }}>
          + Add Weigh-in
        </button>
      ) : (
        <div className="weight-add-bar">
          <input
            className="weight-value"
            type="number"
            min="0"
            step="0.1"
            placeholder="kg"
            value={newWeight}
            onChange={(e) => setNewWeight(e.target.value)}
          />
          <input
            className="weight-date"
            type="date"
            value={newDate}
            onChange={(e) => setNewDate(e.target.value)}
          />
          <button className="weight-add-btn" onClick={addWeight} disabled={saving}>
            {saving ? '…' : 'Save'}
          </button>
          <button className="weight-add-cancel" onClick={() => setAdding(false)}>Cancel</button>
        </div>
      )}

      {loading ? (
        <p className="settings-muted" style={{ textAlign: 'center' }}>Loading…</p>
      ) : weights.length === 0 ? (
        <p className="weight-empty">No weigh-ins yet. Add your first one above.</p>
      ) : (
        <ul className="weight-list">
          {weights.map((w) => (
            <li key={w.id} className="weight-row">
              <div className="weight-row-main">
                <div className="weight-row-value">{w.weight} kg</div>
                <div className="weight-row-date">{formatDate(w.recordedAtUtc)}</div>
              </div>
              {renderDelta(w.deltaPercent)}
              <button className="weight-row-delete" onClick={() => deleteWeight(w.id)}>Delete</button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export default WeightPage;
