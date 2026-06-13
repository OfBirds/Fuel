import { useEffect, useState } from 'react';
import { useAuth } from '../context/AuthContext';
import '../styles/settings.css';

interface Prefs {
  notifyReleases: boolean;
}

function SettingsPage() {
  const { user } = useAuth();
  const [prefs, setPrefs] = useState<Prefs | null>(null);
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
        if (alive) setPrefs(data);
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
    </div>
  );
}

export default SettingsPage;
