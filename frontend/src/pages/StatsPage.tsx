import { useEffect, useMemo, useState, useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { useAuth } from '../context/AuthContext';
import { getStatsRange, saveStatsRange, type StatsRange } from '../lib/storage';
import '../styles/stats.css';

interface EntryItem {
  id: string;
  intakeAtUtc: string;
  calories: number;
}

interface PrefsResponse {
  dailyCalorieGoal: number | null;
}

interface Bucket {
  label: string;
  calories: number; // total registered calories in the bucket
  days: number; // calendar days the bucket spans — scales the (daily) goal line
}

const RANGES: StatsRange[] = ['week', 'month', 'year'];
const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

const pad = (n: number) => String(n).padStart(2, '0');

// Local calendar date key (YYYY-MM-DD). Buckets and summaries are computed against the
// viewer's local calendar — entries are stored UTC but lived in local time.
function toDateKey(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

// Monday of the week containing d (weeks run Mon–Sun), at local midnight.
function startOfWeek(d: Date): Date {
  const s = new Date(d.getFullYear(), d.getMonth(), d.getDate());
  const offset = (s.getDay() + 6) % 7; // days since Monday (getDay: 0=Sun)
  s.setDate(s.getDate() - offset);
  return s;
}

// The [start, end) local-time bounds of the period the anchor falls in.
function rangeBounds(range: StatsRange, anchor: Date): { start: Date; end: Date } {
  if (range === 'week') {
    const start = startOfWeek(anchor);
    const end = new Date(start);
    end.setDate(end.getDate() + 7);
    return { start, end };
  }
  if (range === 'month') {
    return {
      start: new Date(anchor.getFullYear(), anchor.getMonth(), 1),
      end: new Date(anchor.getFullYear(), anchor.getMonth() + 1, 1),
    };
  }
  return {
    start: new Date(anchor.getFullYear(), 0, 1),
    end: new Date(anchor.getFullYear() + 1, 0, 1),
  };
}

function periodLabel(range: StatsRange, anchor: Date): string {
  if (range === 'week') {
    const s = startOfWeek(anchor);
    const e = new Date(s);
    e.setDate(e.getDate() + 6);
    const fmt = (d: Date) => d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
    return `${fmt(s)} – ${fmt(e)}`;
  }
  if (range === 'month') {
    return anchor.toLocaleDateString('en-US', { month: 'long', year: 'numeric' });
  }
  return String(anchor.getFullYear());
}

function shiftAnchor(range: StatsRange, anchor: Date, delta: number): Date {
  const d = new Date(anchor);
  if (range === 'week') d.setDate(d.getDate() + 7 * delta);
  else if (range === 'month') d.setMonth(d.getMonth() + delta);
  else d.setFullYear(d.getFullYear() + delta);
  return d;
}

// Sum each day's calories (local date) → map keyed by YYYY-MM-DD.
function dailyTotals(entries: EntryItem[]): Map<string, number> {
  const m = new Map<string, number>();
  for (const e of entries) {
    const key = toDateKey(new Date(e.intakeAtUtc));
    m.set(key, (m.get(key) ?? 0) + e.calories);
  }
  return m;
}

// Chart buckets: per-day for a week, per-week for a month, per-month for a year.
// Each bar is the bucket's *total* registered calories.
function buildBuckets(range: StatsRange, anchor: Date, daily: Map<string, number>): Bucket[] {
  if (range === 'week') {
    const start = startOfWeek(anchor);
    return WEEKDAYS.map((label, i) => {
      const d = new Date(start);
      d.setDate(d.getDate() + i);
      return { label, calories: daily.get(toDateKey(d)) ?? 0, days: 1 };
    });
  }

  if (range === 'month') {
    const year = anchor.getFullYear();
    const month = anchor.getMonth();
    const daysInMonth = new Date(year, month + 1, 0).getDate();
    const buckets: Bucket[] = [];
    let cur: { first: number; last: number; cal: number } | null = null;
    let curMonday: string | null = null;
    for (let day = 1; day <= daysInMonth; day++) {
      const d = new Date(year, month, day);
      const mondayKey = toDateKey(startOfWeek(d));
      const cal = daily.get(toDateKey(d)) ?? 0;
      if (mondayKey !== curMonday) {
        if (cur) buckets.push({ label: `${cur.first}–${cur.last}`, calories: cur.cal, days: cur.last - cur.first + 1 });
        cur = { first: day, last: day, cal };
        curMonday = mondayKey;
      } else {
        cur!.last = day;
        cur!.cal += cal;
      }
    }
    if (cur) buckets.push({ label: `${cur.first}–${cur.last}`, calories: cur.cal, days: cur.last - cur.first + 1 });
    return buckets;
  }

  // year → 12 month bars
  const year = anchor.getFullYear();
  return MONTHS.map((label, mi) => {
    const daysInMonth = new Date(year, mi + 1, 0).getDate();
    let cal = 0;
    for (let day = 1; day <= daysInMonth; day++) {
      cal += daily.get(toDateKey(new Date(year, mi, day))) ?? 0;
    }
    return { label, calories: cal, days: daysInMonth };
  });
}

interface Summary {
  avg: number | null;
  max: number | null;
  min: number | null;
  days: number;
}

// Per-day summary across the days actually logged in the period. Always per-day so it can be
// compared against the (daily) goal, regardless of which range the chart is bucketing by.
function summarize(daily: Map<string, number>): Summary {
  const vals = [...daily.values()].filter((v) => v > 0);
  if (vals.length === 0) return { avg: null, max: null, min: null, days: 0 };
  const sum = vals.reduce((a, b) => a + b, 0);
  return {
    avg: Math.round(sum / vals.length),
    max: Math.max(...vals),
    min: Math.min(...vals),
    days: vals.length,
  };
}

// Progress ring — same conic-gradient treatment as the day view's calorie ring.
function Ring({ value, goal }: { value: number | null; goal: number | null }) {
  const has = goal != null && goal > 0 && value != null;
  const pct = has ? (value! / goal!) * 100 : 0;
  const level = pct >= 99 ? 'over' : pct >= 80 ? 'warn' : '';
  return (
    <div
      className={level ? `stat-ring ${level}` : 'stat-ring'}
      style={{ '--pct': has ? Math.min(100, Math.round(pct)) : 0 } as React.CSSProperties}
    >
      <span className="stat-ring-pct">{has ? `${Math.round(pct)}%` : '—'}</span>
    </div>
  );
}

function StatRow({ label, value, goal }: { label: string; value: number | null; goal: number | null }) {
  return (
    <div className="stat-row">
      <div className="stat-cell stat-cell-left">
        <span className="stat-cell-label">{label}</span>
        <span className="stat-cell-value">{value != null ? value : '—'}</span>
      </div>
      <div className="stat-cell stat-cell-center">
        <span className="stat-cell-label">Goal</span>
        <span className="stat-cell-value">{goal != null ? goal : '—'}</span>
      </div>
      <div className="stat-cell stat-cell-right">
        <Ring value={value} goal={goal} />
      </div>
    </div>
  );
}

function StatsPage() {
  const { user } = useAuth();
  const [range, setRange] = useState<StatsRange>(getStatsRange);
  const [anchor, setAnchor] = useState(() => new Date());
  const [entries, setEntries] = useState<EntryItem[]>([]);
  const [goal, setGoal] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const fetchEntries = useCallback(async () => {
    if (!user) return;
    setLoading(true);
    setError(null);
    try {
      const { start, end } = rangeBounds(range, anchor);
      const res = await apiFetch(
        `/api/user/${user.id}/entries?from=${encodeURIComponent(start.toISOString())}&to=${encodeURIComponent(end.toISOString())}`,
      );
      if (!res.ok) throw new Error('failed');
      setEntries((await res.json()) as EntryItem[]);
    } catch {
      setError("Couldn't load your stats. Please try again.");
    } finally {
      setLoading(false);
    }
  }, [user, range, anchor]);

  useEffect(() => {
    fetchEntries();
  }, [fetchEntries]);

  // Goal is the daily target from settings; load it once.
  useEffect(() => {
    if (!user) return;
    let alive = true;
    (async () => {
      try {
        const res = await apiFetch(`/api/user/${user.id}/prefs`);
        if (alive && res.ok) {
          const p = (await res.json()) as PrefsResponse;
          setGoal(p.dailyCalorieGoal ?? null);
        }
      } catch { /* goal is optional context */ }
    })();
    return () => { alive = false; };
  }, [user]);

  const daily = useMemo(() => dailyTotals(entries), [entries]);
  const buckets = useMemo(() => buildBuckets(range, anchor, daily), [range, anchor, daily]);
  const summary = useMemo(() => summarize(daily), [daily]);
  // Chart scale tops out at the tallest of the bars *and* goal markers (so a goal line above
  // every bar still fits), with a little headroom above.
  const scaleMax = useMemo(() => {
    const vals = buckets.map((b) => b.calories);
    if (goal != null && goal > 0) vals.push(...buckets.map((b) => goal * b.days));
    return Math.max(1, ...vals) * 1.08;
  }, [buckets, goal]);

  const changeRange = (r: StatsRange) => {
    setRange(r);
    saveStatsRange(r);
  };

  const { start: periodStart, end: periodEnd } = rangeBounds(range, anchor);
  const now = new Date();
  const atCurrentPeriod = now >= periodStart && now < periodEnd;

  if (!user) return null;

  return (
    <div className="stats-page">
      <h1 className="stats-title">Stats</h1>

      <div className="stats-controls">
        <div className="stats-range-toggle" role="tablist" aria-label="Stats range">
          {RANGES.map((r) => (
            <button
              key={r}
              type="button"
              role="tab"
              aria-selected={r === range}
              className={r === range ? 'stats-range-btn active' : 'stats-range-btn'}
              onClick={() => changeRange(r)}
            >
              {r[0].toUpperCase() + r.slice(1)}
            </button>
          ))}
        </div>
        <div className="stats-period-nav">
          <button className="stats-nav-btn" onClick={() => setAnchor(shiftAnchor(range, anchor, -1))} aria-label="Previous period">‹</button>
          <span className="stats-period-label">{periodLabel(range, anchor)}</span>
          <button
            className="stats-nav-btn"
            onClick={() => setAnchor(shiftAnchor(range, anchor, 1))}
            aria-label="Next period"
            disabled={atCurrentPeriod}
          >›</button>
        </div>
      </div>

      {error && <p className="form-error" role="alert">{error}</p>}

      <div className="stats-summary">
        <StatRow label="Daily avg" value={summary.avg} goal={goal} />
        <StatRow label="Max day" value={summary.max} goal={goal} />
        <StatRow label="Min day" value={summary.min} goal={goal} />
      </div>

      {loading ? (
        <p className="settings-muted" style={{ textAlign: 'center', marginTop: '2rem' }}>Loading…</p>
      ) : (
        <>
          <div className="stats-chart">
            <div
              className="stats-bars"
              role="img"
              aria-label={`Registered calories by ${range === 'week' ? 'day' : range === 'month' ? 'week' : 'month'}, with the daily goal as a dashed line`}
            >
              {buckets.map((b, i) => {
                const bucketGoal = goal != null && goal > 0 ? goal * b.days : null;
                return (
                  <div className="stats-bar-col" key={i}>
                    {bucketGoal != null && (
                      <div
                        className="stats-goal-line"
                        style={{ bottom: `${Math.min(100, (bucketGoal / scaleMax) * 100)}%` }}
                        title={`Goal: ${bucketGoal} cal`}
                      />
                    )}
                    <div className="stats-bar-track">
                      <div
                        className={`stats-bar-fill${bucketGoal != null && b.calories > bucketGoal ? ' over' : ''}`}
                        style={{ height: `${(b.calories / scaleMax) * 100}%` }}
                        title={`${b.label}: ${b.calories} cal`}
                      >
                        {b.calories > 0 && <span className="stats-bar-value">{b.calories}</span>}
                      </div>
                    </div>
                  </div>
                );
              })}
            </div>
            <div className="stats-axis">
              {buckets.map((b, i) => (
                <span className="stats-bar-label" key={i}>{b.label}</span>
              ))}
            </div>
            {goal != null && goal > 0 && (
              <div className="stats-goal-legend">
                <span className="stats-goal-swatch" aria-hidden="true" /> Goal · {goal}/day
              </div>
            )}
          </div>
          {summary.days === 0 && (
            <p className="settings-muted" style={{ textAlign: 'center', marginTop: '1rem' }}>
              No meals logged in this period.
            </p>
          )}
        </>
      )}
    </div>
  );
}

export default StatsPage;
