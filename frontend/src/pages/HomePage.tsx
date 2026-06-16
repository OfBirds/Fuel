import { useEffect, useState, useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { useNavigate, useLocation } from 'react-router-dom';
import { useAuth } from '../context/AuthContext';
import '../styles/dayview.css';

interface EntryItem {
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
  source: string;
  aiConfidence: number | null;
}

interface PrefsResponse {
  notifyReleases: boolean;
  dailyCalorieGoal: number | null;
}

const MEAL_ORDER = ['Breakfast', 'Lunch', 'Dinner', 'Snack'];

function formatDate(d: Date): string {
  return d.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' });
}

// Local HH:MM for the meal-finished marker.
function formatTime(utc: string): string {
  const d = new Date(utc);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

// Local calendar date (YYYY-MM-DD) — NOT toISOString(), which would shift to UTC.
function toDateString(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

// The viewer's local day as a [from, to) pair of UTC instants. The browser handles
// the offset (and DST), so the day view shows the day as the user actually lived it.
function localDayRangeUtc(d: Date): { from: string; to: string } {
  const start = new Date(d.getFullYear(), d.getMonth(), d.getDate(), 0, 0, 0, 0);
  const end = new Date(start);
  end.setDate(end.getDate() + 1);
  return { from: start.toISOString(), to: end.toISOString() };
}

function HomePage() {
  const { user } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [currentDate, setCurrentDate] = useState(() => new Date());
  const [entries, setEntries] = useState<EntryItem[]>([]);
  const [goal, setGoal] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const dateStr = toDateString(currentDate);

  const fetchData = useCallback(async () => {
    if (!user) return;
    setLoading(true);
    setError(null);
    try {
      const { from, to } = localDayRangeUtc(currentDate);
      const [entriesRes, prefsRes] = await Promise.all([
        apiFetch(`/api/user/${user.id}/entries?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}`),
        apiFetch(`/api/user/${user.id}/prefs`),
      ]);
      if (!entriesRes.ok) throw new Error('Failed to load entries');
      const entriesData = (await entriesRes.json()) as EntryItem[];
      setEntries(entriesData);

      if (prefsRes.ok) {
        const prefsData = (await prefsRes.json()) as PrefsResponse;
        setGoal(prefsData.dailyCalorieGoal ?? null);
      }
    } catch {
      setError("Couldn't load your food log. Please try again.");
    } finally {
      setLoading(false);
    }
  }, [user, currentDate]);

  useEffect(() => {
    fetchData();
  }, [fetchData, location.key]);

  const goDay = (delta: number) => {
    const d = new Date(currentDate);
    d.setDate(d.getDate() + delta);
    setCurrentDate(d);
  };

  const goToday = () => setCurrentDate(new Date());

  const totalCalories = entries.reduce((sum, e) => sum + e.calories, 0);

  const grouped = MEAL_ORDER.map((meal) => {
    const mealEntries = entries.filter((e) => e.mealType === meal);
    const mealTotal = mealEntries.reduce((sum, e) => sum + e.calories, 0);
    // The "finished at" marker is the latest intake time in the meal. Only meaningful
    // for the main meals (snacks are scattered through the day, so no single time).
    const finishedAt = meal !== 'Snack' && mealEntries.length > 0
      ? formatTime(mealEntries.reduce((a, b) => (a.intakeAtUtc > b.intakeAtUtc ? a : b)).intakeAtUtc)
      : null;
    return { meal, entries: mealEntries, mealTotal, finishedAt };
  });

  // Calorie bar shifts to warning at 80% of goal and to error at 99%+ — a glanceable
  // "you're near / over your budget" without needing to read the number.
  const goalPct = goal != null && goal > 0 ? (totalCalories / goal) * 100 : 0;
  const calorieLevel = goalPct >= 99 ? 'over' : goalPct >= 80 ? 'warn' : '';

  const deleteEntry = async (entryId: string) => {
    if (!user || !confirm('Delete this entry?')) return;
    const previous = entries;
    setEntries((prev) => prev.filter((e) => e.id !== entryId));
    try {
      const res = await apiFetch(`/api/user/${user.id}/entries/${entryId}`, { method: 'DELETE' });
      if (!res.ok) throw new Error('Delete failed');
    } catch {
      setEntries(previous);
      setError("Couldn't delete that entry. Please try again.");
    }
  };

  const addEntry = (meal: string) => {
    navigate(`/entry/new?meal=${meal}&date=${dateStr}`);
  };

  if (!user) return null;

  return (
    <div className="day-view">
      <div className="day-header">
        <button className="day-nav-btn" onClick={() => goDay(-1)} aria-label="Previous day">&larr;</button>
        <span className="day-date">{formatDate(currentDate)}</span>
        <button className="day-nav-btn" onClick={() => goDay(1)} aria-label="Next day">&rarr;</button>
        <button className="day-today-btn" onClick={goToday}>Today</button>
      </div>

      <div className="calorie-bar">
        <div className="calorie-bar-label">
          <span>Calories: {totalCalories}{goal != null ? ` / ${goal}` : ''}</span>
          {goal != null && goal > 0 && (
            <span className={calorieLevel ? `calorie-pct ${calorieLevel}` : 'calorie-pct'}>
              {Math.round(goalPct)}%
            </span>
          )}
        </div>
        <div className="calorie-bar-track">
          <div
            className={calorieLevel ? `calorie-bar-fill ${calorieLevel}` : 'calorie-bar-fill'}
            style={{ width: `${Math.min(100, goalPct)}%` }}
          />
        </div>
      </div>

      {error && <p className="form-error" role="alert">{error}</p>}

      {loading ? (
        <p className="settings-muted" style={{ textAlign: 'center', marginTop: '2rem' }}>Loading…</p>
      ) : (
        <>
          {entries.length === 0 && (
            <div className="empty-state">
              <h2>Nothing logged yet</h2>
              <p>Tap the + on any meal below to log your first meal of the day.</p>
            </div>
          )}
          {grouped.map(({ meal, entries: mealEntries, mealTotal, finishedAt }) => (
            <section key={meal} className="meal-section" aria-labelledby={`meal-${meal}`}>
              <div className="meal-section-header">
                <h2 id={`meal-${meal}`} className="meal-section-title">
                  {meal}
                  {finishedAt && <span className="meal-section-time"> ({finishedAt})</span>}
                </h2>
                <div className="meal-section-header-right">
                  <span className="meal-section-calories">{mealTotal} cal</span>
                  <button
                    className="meal-add-btn"
                    onClick={() => addEntry(meal)}
                    aria-label={`Add ${meal}`}
                  >+</button>
                </div>
              </div>
              {mealEntries.map((entry) => (
                <div key={entry.id} className="entry-row">
                  <div className="entry-row-main">
                    <div className="entry-row-name">{entry.foodName}</div>
                    <div className="entry-row-qty">{entry.quantity} {entry.uoM}</div>
                  </div>
                  <span className="entry-row-calories">{entry.calories} cal</span>
                  <div className="entry-row-actions">
                    <button
                      className="entry-icon-btn edit"
                      onClick={() => navigate(`/entry/${entry.id}/edit`)}
                      aria-label="Edit entry"
                      title="Edit"
                    >✎</button>
                    <button
                      className="entry-icon-btn del"
                      onClick={() => deleteEntry(entry.id)}
                      aria-label="Delete entry"
                      title="Delete"
                    >✕</button>
                  </div>
                </div>
              ))}
            </section>
          ))}
        </>
      )}
    </div>
  );
}

export default HomePage;
