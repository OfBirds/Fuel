import { useEffect, useState, useCallback } from 'react';
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

function toDateString(d: Date): string {
  return d.toISOString().slice(0, 10);
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
      const [entriesRes, prefsRes] = await Promise.all([
        fetch(`/api/user/${user.id}/entries?date=${dateStr}`),
        fetch(`/api/user/${user.id}/prefs`),
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
  }, [user, dateStr]);

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
    return { meal, entries: mealEntries, mealTotal };
  });

  const deleteEntry = async (entryId: string) => {
    if (!user || !confirm('Delete this entry?')) return;
    const previous = entries;
    setEntries((prev) => prev.filter((e) => e.id !== entryId));
    try {
      const res = await fetch(`/api/user/${user.id}/entries/${entryId}`, { method: 'DELETE' });
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
            <span>{Math.round((totalCalories / goal) * 100)}%</span>
          )}
        </div>
        <div className="calorie-bar-track">
          <div
            className="calorie-bar-fill"
            style={{ width: `${goal != null && goal > 0 ? Math.min(100, (totalCalories / goal) * 100) : 0}%` }}
          />
        </div>
      </div>

      {error && <p className="form-error" role="alert">{error}</p>}

      {loading ? (
        <p className="settings-muted" style={{ textAlign: 'center', marginTop: '2rem' }}>Loading…</p>
      ) : entries.length === 0 ? (
        <div className="empty-state">
          <h2>Nothing logged yet</h2>
          <p>Use an “+ Add” button below to log your first meal of the day.</p>
        </div>
      ) : (
        grouped.map(({ meal, entries: mealEntries, mealTotal }) => (
          <section key={meal} className="meal-section" aria-labelledby={`meal-${meal}`}>
            <div className="meal-section-header">
              <h2 id={`meal-${meal}`} className="meal-section-title">{meal}</h2>
              <span className="meal-section-calories">{mealTotal} cal</span>
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
                    className="entry-row-btn"
                    onClick={() => navigate(`/entry/${entry.id}/edit`)}
                    aria-label="Edit entry"
                  >Edit</button>
                  <button
                    className="entry-row-btn danger"
                    onClick={() => deleteEntry(entry.id)}
                    aria-label="Delete entry"
                  >Del</button>
                </div>
              </div>
            ))}
            <button className="add-entry-btn" onClick={() => addEntry(meal)}>+ Add {meal}</button>
          </section>
        ))
      )}
    </div>
  );
}

export default HomePage;
