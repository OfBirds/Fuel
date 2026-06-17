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
