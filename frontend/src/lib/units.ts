// Unit-of-measure options for the food catalogue.
//
// COMMON_UNITS is the historical flat list — what the picker shows when the grouped
// units pref (Settings) is off. UNIT_GROUPS expands it into Common / Metric / Imperial
// / Other for the grouped picker (default on); the Common group keeps g / ml / piece on
// top so the simple choices stay one click away.

export const COMMON_UNITS = ['g', 'ml', 'piece'];

export interface UnitGroup {
  label: string;
  units: string[];
}

export const UNIT_GROUPS: UnitGroup[] = [
  { label: 'Common', units: ['g', 'ml', 'piece'] },
  { label: 'Metric', units: ['kg', 'l', 'mg'] },
  { label: 'Imperial', units: ['oz', 'lb', 'fl oz', 'cup'] },
  { label: 'Other', units: ['slice', 'serving', 'tbsp', 'tsp'] },
];

export const ALL_UNITS = UNIT_GROUPS.flatMap((g) => g.units);

// ── Reference quantities (docs/food-catalogue-and-logging.md §reference quantities) ──────────────────────────────────────────────
// The catalogue stores nutrition per single unit, but nobody reasons in "per 1 g" — the
// world-standard comparison basis is per 100 g / per 100 ml (EU 1169/2011, Codex CAC/GL
// 2-1985), with US-customary practice quoting solids per 1 oz and beverages per 8 fl oz
// (FDA 21 CFR 101.9 RACCs). This table is the single source of truth for that display
// basis; unknown/legacy units default to a plain "per 1 ⟨unit⟩".
const REF_QTY: Record<string, number> = {
  g: 100,
  ml: 100,
  mg: 1000,
  kg: 1,
  l: 1,
  oz: 1,
  lb: 1,
  cup: 1,
  'fl oz': 8,
};

const REF_LABEL: Record<string, string> = {
  g: 'per 100 g',
  ml: 'per 100 ml',
  mg: 'per 1000 mg',
  kg: 'per 1 kg',
  l: 'per 1 l',
  oz: 'per 1 oz',
  lb: 'per 1 lb',
  cup: 'per 1 cup',
  'fl oz': 'per 8 fl oz',
};

export function refQty(uom: string): number {
  return REF_QTY[uom] ?? 1;
}

export function refLabel(uom: string): string {
  return REF_LABEL[uom] ?? `per 1 ${uom}`;
}

// ── Unit-system inference + conversion (docs/food-catalogue-and-logging.md) ────────────────────────────────
// Whenever a value is produced by the system (AI estimate, barcode/OFF lookup) rather than
// typed by the user, it's converted to the user's inferred preferred unit system before
// display. Countable/ad-hoc units never vote and are never converted.
export type UnitSystem = 'metric' | 'imperial' | 'neutral';

export const UNIT_SYSTEM: Record<string, UnitSystem> = {
  g: 'metric', kg: 'metric', mg: 'metric', ml: 'metric', l: 'metric',
  oz: 'imperial', lb: 'imperial', 'fl oz': 'imperial', cup: 'imperial',
  piece: 'neutral', slice: 'neutral', serving: 'neutral', tbsp: 'neutral', tsp: 'neutral',
};

function unitSystemOf(uom: string): UnitSystem {
  return UNIT_SYSTEM[uom] ?? 'neutral';
}

export interface UsageFood {
  defaultUoM: string;
  usageCount: number | null;
}

// Usage-weighted vote across a user's catalogue: each food votes for its unit's system,
// weighted by how often it's actually been logged (unused foods still count once). Neutral
// units never vote. Ties (including no data at all) default to metric — the app's own
// g/ml defaults.
export function inferPreferredSystem(foods: UsageFood[]): 'metric' | 'imperial' {
  let metric = 0;
  let imperial = 0;
  for (const f of foods) {
    const system = unitSystemOf(f.defaultUoM);
    if (system === 'neutral') continue;
    const weight = f.usageCount && f.usageCount > 0 ? f.usageCount : 1;
    if (system === 'metric') metric += weight;
    else imperial += weight;
  }
  return imperial > metric ? 'imperial' : 'metric';
}

export interface ConvertibleRow {
  quantity: number;
  uom: string;
}

// Standard factors, applied to quantity + unit only — the row's absolute calories/macros
// describe the same physical food and carry over unchanged.
const CONVERT_TO_METRIC: Record<string, { uom: string; factor: number }> = {
  oz: { uom: 'g', factor: 28.35 },
  lb: { uom: 'kg', factor: 0.4536 },
  'fl oz': { uom: 'ml', factor: 29.57 },
  cup: { uom: 'ml', factor: 240 },
};

const CONVERT_TO_IMPERIAL: Record<string, { uom: string; factor: number }> = {
  g: { uom: 'oz', factor: 1 / 28.35 },
  kg: { uom: 'lb', factor: 1 / 0.4536 },
  ml: { uom: 'fl oz', factor: 1 / 29.57 },
  l: { uom: 'fl oz', factor: 33.81 },
};

// Converts a system-produced row's quantity + unit into the target system. tbsp/tsp/piece/
// slice/serving (and any unit with no entry in the table above, e.g. already-metric mg/kg
// when the target is metric) pass through untouched.
export function convertToSystem<T extends ConvertibleRow>(row: T, system: 'metric' | 'imperial'): T {
  const table = system === 'metric' ? CONVERT_TO_METRIC : CONVERT_TO_IMPERIAL;
  const conversion = table[row.uom];
  if (!conversion) return row;
  return {
    ...row,
    quantity: Math.round(row.quantity * conversion.factor * 10) / 10,
    uom: conversion.uom,
  };
}
