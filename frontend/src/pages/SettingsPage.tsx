import { useEffect, useState } from 'react';
import { apiFetch } from '../lib/api';
import { useAuth } from '../context/AuthContext';
import '../styles/settings.css';

interface Prefs {
  notifyReleases: boolean;
  dailyCalorieGoal: number | null;
}

interface ProfileData {
  height: number | null;
  sex: string | null;
  constitution: string | null;
  yearOfBirth: number | null;
  activityLevel: string | null;
  mealPauseHours: number | null;
  mealPauseScope: string | null;
  showMacros: boolean;
}

interface MetabolismData {
  bmr: number;
  tdee: number;
  bmi: number;
  idealWeightMin: number | null;
  idealWeightMax: number | null;
  activityLevel: string;
}

const ACTIVITY_OPTIONS = [
  { value: 'sedentary', label: 'Sedentary' },
  { value: 'light', label: 'Light' },
  { value: 'moderate', label: 'Moderate' },
  { value: 'active', label: 'Active' },
  { value: 'very_active', label: 'Very Active' },
];

function SettingsPage() {
  const { user } = useAuth();
  const [prefs, setPrefs] = useState<Prefs | null>(null);
  const [profile, setProfile] = useState<ProfileData | null>(null);
  const [metabolism, setMetabolism] = useState<MetabolismData | null>(null);
  const [metaLoading, setMetaLoading] = useState(false);
  const [goalDraft, setGoalDraft] = useState('');
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Draft states for debounced profile fields
  const [hDraft, setHDraft] = useState('');
  const [yobDraft, setYobDraft] = useState('');
  const [pauseDraft, setPauseDraft] = useState('');

  // Help me decide
  const [showWrist, setShowWrist] = useState(false);
  const [wristCm, setWristCm] = useState('');
  const [computedFrame, setComputedFrame] = useState('');

  useEffect(() => {
    if (!user) return;
    let alive = true;
    (async () => {
      try {
        const [prefsRes, profileRes] = await Promise.all([
          apiFetch(`/api/user/${user.id}/prefs`),
          apiFetch(`/api/user/${user.id}/profile`),
        ]);
        if (alive) {
          if (prefsRes.ok) {
            const p = (await prefsRes.json()) as Prefs;
            setPrefs(p);
            setGoalDraft(p.dailyCalorieGoal?.toString() ?? '');
          }
          if (profileRes.ok) {
            const pr = (await profileRes.json()) as ProfileData;
            setProfile(pr);
            setHDraft(pr.height?.toString() ?? '');
            setYobDraft(pr.yearOfBirth?.toString() ?? '');
            setPauseDraft(pr.mealPauseHours?.toString() ?? '');
          }
        }
      } catch {
        if (alive) setError("Couldn't load your settings. Please try again.");
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [user]);

  // Fetch metabolism when profile is loaded (needs weight)
  useEffect(() => {
    if (!user || !profile) return;
    let alive = true;
    setMetaLoading(true);
    (async () => {
      try {
        const res = await apiFetch(`/api/user/${user.id}/metabolism`);
        if (alive && res.ok) setMetabolism((await res.json()) as MetabolismData);
      } catch { /* metabolism is optional display */ }
      finally { if (alive) setMetaLoading(false); }
    })();
    return () => { alive = false; };
  }, [user, profile]);

  // Constitution helper
  useEffect(() => {
    if (wristCm && profile?.height) {
      const h = profile.height;
      const w = Number(wristCm);
      if (h > 0 && w > 0) {
        const r = h / w;
        if (profile.sex === 'Female') {
          if (r > 11) setComputedFrame('Small');
          else if (r >= 10.1) setComputedFrame('Medium');
          else setComputedFrame('Large');
        } else {
          if (r > 10.4) setComputedFrame('Small');
          else if (r >= 9.6) setComputedFrame('Medium');
          else setComputedFrame('Large');
        }
      } else {
        setComputedFrame('');
      }
    }
  }, [wristCm, profile?.height, profile?.sex]);

  const updatePrefs = async (next: Prefs) => {
    if (!user) return;
    const previous = prefs;
    setPrefs(next);
    setSaving(true);
    setError(null);
    try {
      const res = await apiFetch(`/api/user/${user.id}/prefs`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(next),
      });
      if (!res.ok) throw new Error('save failed');
      setPrefs((await res.json()) as Prefs);
    } catch {
      setPrefs(previous);
      setError("Couldn't save that change. Please try again.");
    } finally {
      setSaving(false);
    }
  };

  const updateProfile = async (next: Partial<ProfileData>) => {
    if (!user || !profile) return;
    const previous = profile;
    const merged = { ...profile, ...next };
    setProfile(merged);
    setSaving(true);
    setError(null);
    try {
      const res = await apiFetch(`/api/user/${user.id}/profile`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(next),
      });
      if (!res.ok) throw new Error('save failed');
      setProfile((await res.json()) as ProfileData);
    } catch {
      setProfile(previous);
      setError("Couldn't save that change. Please try again.");
    } finally {
      setSaving(false);
    }
  };

  const commitGoal = () => {
    if (!prefs) return;
    const trimmed = goalDraft.trim();
    const val = trimmed === '' ? null : Number(trimmed);
    if (val !== null && (Number.isNaN(val) || val < 0)) {
      setGoalDraft(prefs.dailyCalorieGoal?.toString() ?? '');
      return;
    }
    if (val === prefs.dailyCalorieGoal) return;
    updatePrefs({ ...prefs, dailyCalorieGoal: val });
  };

  const commitHeight = () => {
    if (!profile) return;
    const val = hDraft.trim() ? Number(hDraft.trim()) : null;
    if (val !== null && (Number.isNaN(val) || val <= 0)) { setHDraft(profile.height?.toString() ?? ''); return; }
    if (val === profile.height) return;
    updateProfile({ height: val });
  };

  const commitYob = () => {
    if (!profile) return;
    const val = yobDraft.trim() ? Number(yobDraft.trim()) : null;
    if (val !== null && (Number.isNaN(val) || val < 1900)) { setYobDraft(profile.yearOfBirth?.toString() ?? ''); return; }
    if (val === profile.yearOfBirth) return;
    updateProfile({ yearOfBirth: val });
  };

  const commitPause = () => {
    if (!profile) return;
    const val = pauseDraft.trim() ? Number(pauseDraft.trim()) : null;
    if (val !== null && (Number.isNaN(val) || val < 0)) { setPauseDraft(profile.mealPauseHours?.toString() ?? ''); return; }
    if (val === profile.mealPauseHours) return;
    updateProfile({ mealPauseHours: val });
  };

  if (!user) return null;

  return (
    <div className="settings-page">
      <h1 className="settings-title">Settings</h1>

      {loading ? (
        <p className="settings-muted">Loading your settings…</p>
      ) : (
        <>
          {/* Notifications */}
          <Section title="Notifications">
            <label className="settings-row" title="Get an email when a new version of Fuel is released.">
              <input
                type="checkbox"
                checked={prefs?.notifyReleases ?? false}
                disabled={saving || !prefs}
                onChange={(e) => prefs && updatePrefs({ ...prefs, notifyReleases: e.target.checked })}
              />
              <span className="settings-row-label">
                Email me about new versions
                <span className="settings-row-help">A short note when Fuel is updated. Nothing else — and no data leaves the app.</span>
              </span>
            </label>
          </Section>

          {/* Daily Goal */}
          <Section title="Daily Goal">
            <label className="settings-row">
              <span className="settings-row-label">
                Daily calorie goal
                <span className="settings-row-help">Your target for the day. Leave empty if you don't want a goal.</span>
              </span>
              <input
                type="number" min="0" step="50"
                value={goalDraft}
                disabled={!prefs}
                onChange={(e) => setGoalDraft(e.target.value)}
                onBlur={commitGoal}
                onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                style={{ width: '100px', textAlign: 'right', marginLeft: 'auto' }}
                placeholder="e.g. 2000"
              />
            </label>
          </Section>

          {/* Profile */}
          <Section title="Profile">
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem' }}>
              <SettingsField label="Height (cm)">
                <input type="number" value={hDraft}
                  onChange={(e) => setHDraft(e.target.value)} onBlur={commitHeight}
                  onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                  placeholder="e.g. 180" />
              </SettingsField>
              <SettingsField label="Sex">
                <select value={profile?.sex ?? ''} onChange={(e) => updateProfile({ sex: e.target.value || null })}>
                  <option value="">—</option>
                  <option value="Male">Male</option>
                  <option value="Female">Female</option>
                </select>
              </SettingsField>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem' }}>
              <SettingsField label="Year of birth">
                <input type="number" value={yobDraft}
                  onChange={(e) => setYobDraft(e.target.value)} onBlur={commitYob}
                  onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                  placeholder="e.g. 1996" />
              </SettingsField>
              <SettingsField label="Activity level">
                <select value={profile?.activityLevel ?? 'sedentary'} onChange={(e) => updateProfile({ activityLevel: e.target.value })}>
                  {ACTIVITY_OPTIONS.map((a) => <option key={a.value} value={a.value}>{a.label}</option>)}
                </select>
              </SettingsField>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem' }}>
              <SettingsField label="Body frame">
                <select value={profile?.constitution ?? ''} onChange={(e) => updateProfile({ constitution: e.target.value || null })}>
                  <option value="">—</option>
                  <option value="Small">Small</option>
                  <option value="Medium">Medium</option>
                  <option value="Large">Large</option>
                </select>
              </SettingsField>
              <div style={{ display: 'flex', alignItems: 'flex-end', paddingBottom: '0.2rem' }}>
                <button type="button" className="settings-row-help" style={{ background: 'none', border: 'none', color: 'var(--primary)', cursor: 'pointer', fontSize: '0.8rem', textDecoration: 'underline', whiteSpace: 'nowrap' }}
                  onClick={() => setShowWrist(!showWrist)}>
                  {showWrist ? 'Hide' : 'Help me decide'}
                </button>
              </div>
              {showWrist && (
                <div style={{ background: 'var(--neutral)', borderRadius: 6, padding: '0.6rem', gridColumn: '1 / -1' }}>
                  <SettingsField label="Wrist circumference (cm)">
                    <input type="number" step="0.1" value={wristCm} onChange={(e) => setWristCm(e.target.value)} placeholder="e.g. 17" />
                  </SettingsField>
                  {computedFrame && (
                    <p style={{ marginTop: '0.4rem', fontWeight: 600, color: 'var(--primary)', fontSize: '0.85rem' }}>
                      Estimated: {computedFrame}
                      <button type="button" style={{ marginLeft: '0.5rem', background: 'none', border: 'none', color: 'var(--primary)', cursor: 'pointer', textDecoration: 'underline' }}
                        onClick={() => { updateProfile({ constitution: computedFrame }); setShowWrist(false); }}>Use this</button>
                    </p>
                  )}
                </div>
              )}
            </div>
          </Section>

          {/* Display */}
          <Section title="Display">
            <label className="settings-row">
              <input type="checkbox"
                checked={profile?.showMacros ?? false}
                disabled={!profile}
                onChange={(e) => profile && updateProfile({ showMacros: e.target.checked })} />
              <span className="settings-row-label">
                Show macros on the home page
                <span className="settings-row-help">See protein, carbs, and fat alongside calorie totals.</span>
              </span>
            </label>
          </Section>

          {/* Meal Pause */}
          <Section title="Meal Pause">
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem' }}>
              <SettingsField label="Pause (hours)">
                <input type="number" min="0" step="0.5" value={pauseDraft}
                  onChange={(e) => setPauseDraft(e.target.value)} onBlur={commitPause}
                  onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur(); }}
                  placeholder="e.g. 4" />
              </SettingsField>
              <SettingsField label="Scope">
                <select value={profile?.mealPauseScope ?? 'non-snack'} onChange={(e) => updateProfile({ mealPauseScope: e.target.value })}>
                  <option value="non-snack">Non-snack only</option>
                  <option value="all">All (incl. snacks)</option>
                </select>
              </SettingsField>
            </div>
            <span className="settings-row-help">
              Second helpings within the same meal are always fine. "Non-snack only" checks only between Breakfast, Lunch, and Dinner.{" "}
              {profile?.mealPauseScope === 'all' && <em>If "all," every snack line also triggers the check — beware.</em>}
            </span>
          </Section>

          {/* Metabolism */}
          <Section title="Metabolism">
            {metaLoading ? (
              <p className="settings-muted">Calculating…</p>
            ) : metabolism ? (
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.5rem' }}>
                <ReadOnlyStat label="BMR" value={`${metabolism.bmr} kcal/day`} />
                <ReadOnlyStat label="TDEE" value={`${metabolism.tdee} kcal/day`} />
                <ReadOnlyStat label="BMI" value={String(metabolism.bmi)} />
                <ReadOnlyStat label="Activity" value={metabolism.activityLevel} />
                {metabolism.idealWeightMin != null && (
                  <ReadOnlyStat label="Ideal weight" value={`${metabolism.idealWeightMin}–${metabolism.idealWeightMax} kg`} />
                )}
              </div>
            ) : (
              <p className="settings-muted">Add a weigh-in and complete your profile to see metabolism stats.</p>
            )}
          </Section>
        </>
      )}

      {error && <p className="settings-error" role="alert">{error}</p>}
    </div>
  );
}

// ── Small helpers ──

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="settings-section" style={{ marginBottom: '1rem' }}>
      <h2 className="settings-section-title">{title}</h2>
      {children}
    </section>
  );
}

function SettingsField({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.2rem' }}>
      <span style={{ fontWeight: 600, fontSize: '0.85rem' }}>{label}</span>
      {children}
    </div>
  );
}

function ReadOnlyStat({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <span className="settings-row-help" style={{ display: 'block' }}>{label}</span>
      <span style={{ fontWeight: 600, fontSize: '0.95rem' }}>{value}</span>
    </div>
  );
}

export default SettingsPage;
