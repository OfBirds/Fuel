import { useState, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import '../styles/onboarding.css';

interface ProfileResponse {
  height: number | null;
  sex: string | null;
  constitution: string | null;
  yearOfBirth: number | null;
  activityLevel: string | null;
  mealPauseHours: number | null;
  mealPauseScope: string | null;
  showMacros: boolean;
}

const ACTIVITY_LEVELS = [
  { value: 'sedentary', label: 'Sedentary (little or no exercise)' },
  { value: 'light', label: 'Light (exercise 1-3 days/week)' },
  { value: 'moderate', label: 'Moderate (exercise 3-5 days/week)' },
  { value: 'active', label: 'Active (exercise 6-7 days/week)' },
  { value: 'very_active', label: 'Very Active (intense exercise daily)' },
];

function OnboardingPage({ onComplete }: { onComplete?: () => void }) {
  const { user } = useAuth();
  const navigate = useNavigate();

  const [height, setHeight] = useState('');
  const [sex, setSex] = useState('');
  const [constitution, setConstitution] = useState('');
  const [yearOfBirth, setYearOfBirth] = useState('');
  const [activityLevel, setActivityLevel] = useState('sedentary');
  const [weight, setWeight] = useState('');
  const [goal, setGoal] = useState('');

  // "Help me decide" constitution
  const [showHelp, setShowHelp] = useState(false);
  const [wristCm, setWristCm] = useState('');
  const [computedFrame, setComputedFrame] = useState('');

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (wristCm && height) {
      const h = Number(height);
      const w = Number(wristCm);
      if (h > 0 && w > 0) {
        const r = h / w;
        if (sex === 'Female') {
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
  }, [wristCm, height, sex]);

  const save = async () => {
    if (!user) return;
    const h = Number(height);
    const y = Number(yearOfBirth);
    const w = Number(weight);
    if (!h || !sex || !constitution || !y || !w) {
      setError('Please fill in all required fields.');
      return;
    }

    setSaving(true);
    setError(null);

    try {
      // 1. Save profile
      const profileRes = await fetch(`/api/user/${user.id}/profile`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ height: h, sex, constitution, yearOfBirth: y, activityLevel }),
      });
      if (!profileRes.ok) throw new Error('Failed to save profile');

      // 2. Save starting weight
      const weightRes = await fetch(`/api/user/${user.id}/weights`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ weight: w }),
      });
      if (!weightRes.ok) throw new Error('Failed to save weight');

      // 3. Save goal if provided
      if (goal) {
        const g = Number(goal);
        if (g > 0) {
          await fetch(`/api/user/${user.id}/prefs`, {
            method: 'PUT',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ notifyReleases: true, dailyCalorieGoal: g }),
          });
        }
      }

      // Lift the onboarding gate in AppContent (its profile check won't re-run on
      // its own — user identity hasn't changed), then land on the day view.
      onComplete?.();
      navigate('/');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Save failed.');
    } finally {
      setSaving(false);
    }
  };

  if (!user) return null;

  return (
    <div className="onboarding-page">
      <div className="onboarding-container">
        <h1>Welcome to Fuel</h1>
        <p className="onboarding-subtitle">Let's set up your profile to get started.</p>

        {error && <p className="onboarding-error" role="alert">{error}</p>}

        <div className="onboarding-form">
          <div className="onboarding-row">
            <div className="onboarding-section">
              <label>Height (cm)</label>
              <input type="number" value={height} onChange={(e) => setHeight(e.target.value)} placeholder="e.g. 180" />
            </div>
            <div className="onboarding-section">
              <label>Sex</label>
              <select value={sex} onChange={(e) => setSex(e.target.value)}>
                <option value="">Select…</option>
                <option value="Male">Male</option>
                <option value="Female">Female</option>
              </select>
            </div>
          </div>

          <div className="onboarding-section">
            <label>Body frame</label>
            <select value={constitution} onChange={(e) => setConstitution(e.target.value)}>
              <option value="">Select…</option>
              <option value="Small">Small</option>
              <option value="Medium">Medium</option>
              <option value="Large">Large</option>
            </select>
            <button className="onboarding-help-link" type="button" onClick={() => setShowHelp(!showHelp)}>
              {showHelp ? 'Hide' : 'Help me decide'}
            </button>
            {showHelp && (
              <div className="onboarding-help">
                <label>Wrist circumference (cm)</label>
                <input type="number" value={wristCm} onChange={(e) => setWristCm(e.target.value)} placeholder="e.g. 17" step="0.1" />
                {computedFrame && (
                  <div className="onboarding-help-result">
                    Estimated frame: {computedFrame}
                    <button type="button" className="onboarding-help-link" style={{ marginLeft: '0.5rem' }} onClick={() => setConstitution(computedFrame)}>Use this</button>
                  </div>
                )}
              </div>
            )}
          </div>

          <div className="onboarding-row">
            <div className="onboarding-section">
              <label>Year of birth</label>
              <input type="number" value={yearOfBirth} onChange={(e) => setYearOfBirth(e.target.value)} placeholder="e.g. 1996" />
            </div>
            <div className="onboarding-section">
              <label>Activity level</label>
              <select value={activityLevel} onChange={(e) => setActivityLevel(e.target.value)}>
                {ACTIVITY_LEVELS.map((a) => <option key={a.value} value={a.value}>{a.label}</option>)}
              </select>
            </div>
          </div>

          <div className="onboarding-row">
            <div className="onboarding-section">
              <label>Starting weight (kg)</label>
              <input type="number" value={weight} onChange={(e) => setWeight(e.target.value)} placeholder="e.g. 80" step="0.1" />
            </div>
            <div className="onboarding-section">
              <label>Daily calorie goal</label>
              <input type="number" value={goal} onChange={(e) => setGoal(e.target.value)} placeholder="e.g. 2000" />
            </div>
          </div>

          <button className="onboarding-submit" onClick={save} disabled={saving}>
            {saving ? 'Saving…' : 'Get Started'}
          </button>
        </div>
      </div>
    </div>
  );
}

export default OnboardingPage;
