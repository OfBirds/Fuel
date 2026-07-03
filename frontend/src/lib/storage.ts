// Centralized localStorage access. The mechanism lives here so it can be swapped
// (e.g. IndexedDB for the PWA offline cache) without touching callers. Add your
// own typed getters/setters below following the same read/write/remove pattern.

const PREFIX = 'app:';

function read<T>(key: string): T | null {
  try {
    const raw = localStorage.getItem(PREFIX + key);
    return raw ? (JSON.parse(raw) as T) : null;
  } catch {
    return null;
  }
}

function write<T>(key: string, value: T): void {
  try {
    localStorage.setItem(PREFIX + key, JSON.stringify(value));
  } catch {
    /* ignore quota / private-mode errors */
  }
}

// --- Auth session ---
// The bearer token + user are persisted by AuthContext under bare (unprefixed) keys so
// the session survives reloads. These accessors are the single read/clear point for the
// token (used by apiFetch) and for tearing the session down on a 401.
export const getToken = (): string | null => {
  try { return localStorage.getItem('token'); } catch { return null; }
};
export const clearSession = (): void => {
  try {
    localStorage.removeItem('token');
    localStorage.removeItem('user');
  } catch { /* ignore */ }
};

// --- Theme ---
export type Theme = 'light' | 'dark';
export const getTheme = () => read<Theme>('theme');
export const saveTheme = (theme: Theme) => write('theme', theme);

// --- Auto-update preference (example boolean pref) ---
export const getAutoUpdate = () => read<boolean>('autoUpdate') ?? true;
export const saveAutoUpdate = (on: boolean) => write('autoUpdate', on);

// --- Font scale (percent, e.g. 100) ---
export const getFontScale = () => read<number>('fontScale') ?? 100;
export const saveFontScale = (pct: number) => write('fontScale', pct);

// --- Spacing scale (percent, e.g. 100) ---
// Multiplies the key paddings/gaps across the app (via the --density CSS var) so users can
// tighten the layout to fit more on screen, or loosen it for comfort. See lib/appearance.ts.
export const getSpacingScale = () => read<number>('spacingScale') ?? 100;
export const saveSpacingScale = (pct: number) => write('spacingScale', pct);

// --- Last used meal type (preselected on entry form) ---
export const getLastMealType = () => read<string>('lastMealType') ?? 'Breakfast';
export const saveLastMealType = (mealType: string) => write('lastMealType', mealType);

// --- Show macros (local mirror of the server profile flag) ---
// Authoritative value lives on the user profile; mirrored here on prefs/profile load
// so macro UI can be gated synchronously without an extra fetch per page.
export const getShowMacros = () => read<boolean>('showMacros') ?? false;
export const saveShowMacros = (on: boolean) => write('showMacros', on);

// --- Grouped unit picker (expanded metric/imperial/other unit list in food forms) ---
export const getGroupedUnits = () => read<boolean>('groupedUnits') ?? true;
export const saveGroupedUnits = (on: boolean) => write('groupedUnits', on);

// --- Food sort order (default order of the catalogue search while logging) ---
// Pure UI preference (no server round-trip), set in Settings and read by the entry form.
export type FoodSortMode = 'priority' | 'alphabetical' | 'most-used' | 'recent';
export const FOOD_SORT_LABELS: Record<FoodSortMode, string> = {
  priority: 'Priority',
  alphabetical: 'A–Z',
  'most-used': 'Most-used',
  recent: 'Recent',
};
export const getFoodSortMode = (): FoodSortMode => read<FoodSortMode>('foodSortMode') ?? 'priority';
export const saveFoodSortMode = (mode: FoodSortMode) => write('foodSortMode', mode);

// --- Onboarding completed flag (set on skip or completion) ---
export const getOnboardingCompleted = () => read<boolean>('onboardingCompleted') ?? false;
export const saveOnboardingCompleted = () => write('onboardingCompleted', true);

// --- Stats range (week / month / year) — remembers the last range picked on the Stats page ---
export type StatsRange = 'week' | 'month' | 'year';
export const getStatsRange = (): StatsRange => read<StatsRange>('statsRange') ?? 'week';
export const saveStatsRange = (range: StatsRange) => write('statsRange', range);
