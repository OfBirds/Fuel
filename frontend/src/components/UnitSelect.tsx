import { getGroupedUnits } from '../lib/storage';
import { ALL_UNITS, COMMON_UNITS, UNIT_GROUPS } from '../lib/units';

interface Props {
  value: string;
  onChange: (value: string) => void;
  id?: string;
  className?: string;
}

// Unit-of-measure picker. Honors the Settings "grouped units" pref: on (default)
// renders the full metric/imperial/other list as <optgroup>s, off falls back to the
// flat g/ml/piece list. The current value is always included as an option so editing a
// food whose unit isn't in the active list (e.g. grouped pref later turned off) never
// blanks the select.
export function UnitSelect({ value, onChange, id, className }: Props) {
  const grouped = getGroupedUnits();
  const known = grouped ? ALL_UNITS : COMMON_UNITS;
  const extra = value && !known.includes(value) ? value : null;
  return (
    <select id={id} className={className} value={value} onChange={(e) => onChange(e.target.value)}>
      {extra && <option value={extra}>{extra}</option>}
      {grouped
        ? UNIT_GROUPS.map((g) => (
            <optgroup key={g.label} label={g.label}>
              {g.units.map((u) => <option key={u} value={u}>{u}</option>)}
            </optgroup>
          ))
        : COMMON_UNITS.map((u) => <option key={u} value={u}>{u}</option>)}
    </select>
  );
}
