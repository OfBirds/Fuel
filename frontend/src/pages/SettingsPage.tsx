import { useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';
import '../styles/settings.css';

interface Prefs {
  notifyReleases: boolean;
  dailyCalorieGoal: number | null;
}

function SettingsPage() {
  const { user } = useAuth();
  const [prefs, setPrefs] = useState<Prefs | null>(null);
  const [goalDraft, setGoalDraft] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!user) return;
    let alive = true;
    (async () => {
      try {
        const res = await fetch(`/api/user/${user.id}/prefs`);
        if (!res.ok) throw new Error('load failed');
        const data = (await res.json()) as Prefs;
        if (alive) {
          setPrefs(data);
          setGoalDraft(data.dailyCalorieGoal?.toString() ?? '');
        }
      } catch {
        if (alive) setError("Couldn't load your settings. Please try again.");
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => {
      alive = false;
    };
  }, [user]);

  const update = async (next: Prefs) => {
    if (!user) return;
    const previous = prefs;
    setPrefs(next); // optimistic
    setSaving(true);
    setError(null);
    try {
      const res = await fetch(`/api/user/${user.id}/prefs`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(next),
      });
      if (!res.ok) throw new Error('save failed');
      setPrefs((await res.json()) as Prefs);
    } catch {
      setPrefs(previous); // roll back on failure — never silently lose the real state
      setError("Couldn't save that change. Please try again.");
    } finally {
      setSaving(false);
    }
  };

  // Persist the goal only when the user finishes editing (blur / Enter) — saving on
  // every keystroke would disable the field mid-typing and drop characters.
  const commitGoal = () => {
    if (!prefs) return;
    const trimmed = goalDraft.trim();
    const val = trimmed === '' ? null : Number(trimmed);
    if (val !== null && (Number.isNaN(val) || val < 0)) {
      setGoalDraft(prefs.dailyCalorieGoal?.toString() ?? ''); // revert invalid input
      return;
    }
    if (val === prefs.dailyCalorieGoal) return; // nothing changed
    update({ ...prefs, dailyCalorieGoal: val });
  };

  if (!user) return null;

  return (
    <div className="settings-page">
      <h1 className="settings-title">Settings</h1>

      {loading ? (
        <p className="settings-muted">Loading your settings…</p>
      ) : (
        <section className="settings-section" aria-labelledby="settings-notifications-heading">
          <h2 id="settings-notifications-heading" className="settings-section-title">
            Notifications
          </h2>

          <label className="settings-row" title="Get an email when a new version of Fuel is released. You can turn this off any time, including from a link in the email.">
            <input
              type="checkbox"
              checked={prefs?.notifyReleases ?? false}
              disabled={saving || !prefs}
              onChange={(e) => prefs && update({ ...prefs, notifyReleases: e.target.checked })}
            />
            <span className="settings-row-label">
              Email me about new versions
              <span className="settings-row-help">
                A short note when Fuel is updated. Nothing else — and no data leaves the app.
              </span>
            </span>
          </label>

          {error && (
            <p className="settings-error" role="alert">
              {error}
            </p>
          )}
        </section>
      )}

      {!loading && (
        <section className="settings-section" aria-labelledby="settings-goal-heading" style={{ marginTop: '1.5rem' }}>
          <h2 id="settings-goal-heading" className="settings-section-title">
            Daily Goal
          </h2>

          <label className="settings-row">
            <span className="settings-row-label">
              Daily calorie goal
              <span className="settings-row-help">
                Your target for the day. Leave empty if you don't want a goal.
              </span>
            </span>
            <input
              type="number"
              min="0"
              step="50"
              value={goalDraft}
              disabled={!prefs}
              onChange={(e) => setGoalDraft(e.target.value)}
              onBlur={commitGoal}
              onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
              style={{ width: '100px', textAlign: 'right' }}
              placeholder="e.g. 2000"
            />
          </label>
        </section>
      )}
    </div>
  );
}

export default SettingsPage;
