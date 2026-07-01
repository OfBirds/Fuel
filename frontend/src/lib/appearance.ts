// Appearance preferences: text size + spacing, applied as CSS custom properties on the
// document root so a single value scales the whole app. Both are persisted as percentages
// (100 = default) via lib/storage. Kept here — not in a component — so App can apply the
// stored values on startup and SettingsPage can apply them live on change.

import {
  getFontScale, saveFontScale, getSpacingScale, saveSpacingScale,
} from './storage';

// Text size — scales `html { font-size }`, so every rem-based size follows.
export const FONT_MIN = 40;
export const FONT_MAX = 200;
export const FONT_STEP = 20;

// Spacing — multiplies the key paddings/gaps wrapped in calc(... * var(--density)).
export const SPACING_MIN = 50;
export const SPACING_MAX = 150;
export const SPACING_STEP = 10;

export const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));

export function applyFontScale(pct: number): void {
  document.documentElement.style.setProperty('--font-scale', String(pct / 100));
}

export function applySpacingScale(pct: number): void {
  document.documentElement.style.setProperty('--density', String(pct / 100));
}

/** Apply the persisted text-size + spacing prefs — call once on app startup. */
export function applyStoredAppearance(): void {
  applyFontScale(getFontScale());
  applySpacingScale(getSpacingScale());
}

/** Set + persist + apply the text size (clamped). Returns the value actually applied. */
export function setFontScale(pct: number): number {
  const v = clamp(pct, FONT_MIN, FONT_MAX);
  applyFontScale(v);
  saveFontScale(v);
  return v;
}

/** Set + persist + apply the spacing (clamped). Returns the value actually applied. */
export function setSpacingScale(pct: number): number {
  const v = clamp(pct, SPACING_MIN, SPACING_MAX);
  applySpacingScale(v);
  saveSpacingScale(v);
  return v;
}
