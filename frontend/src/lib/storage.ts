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

// --- Last used meal type (preselected on entry form) ---
export const getLastMealType = () => read<string>('lastMealType') ?? 'Breakfast';
export const saveLastMealType = (mealType: string) => write('lastMealType', mealType);
