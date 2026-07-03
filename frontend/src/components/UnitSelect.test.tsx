import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { UnitSelect } from './UnitSelect';
import { saveGroupedUnits } from '../lib/storage';

describe('UnitSelect', () => {
  beforeEach(() => localStorage.clear());

  it('renders the flat g/ml/piece list when the grouped pref is off', () => {
    saveGroupedUnits(false);
    render(<UnitSelect value="g" onChange={() => {}} />);
    const opts = screen.getAllByRole('option').map((o) => (o as HTMLOptionElement).value);
    expect(opts).toEqual(['g', 'ml', 'piece']);
  });

  it('renders the grouped metric/imperial/other list when the pref is on (default)', () => {
    render(<UnitSelect value="g" onChange={() => {}} />);
    // optgroups present
    expect(screen.getByRole('group', { name: 'Metric' })).toBeInTheDocument();
    expect(screen.getByRole('group', { name: 'Imperial' })).toBeInTheDocument();
    const opts = screen.getAllByRole('option').map((o) => (o as HTMLOptionElement).value);
    expect(opts).toContain('kg');
    expect(opts).toContain('oz');
    expect(opts).toContain('tbsp');
  });

  it('keeps the current value selectable even when it is not in the active list', () => {
    saveGroupedUnits(false); // flat list = g/ml/piece only
    render(<UnitSelect value="oz" onChange={() => {}} />);
    const opts = screen.getAllByRole('option').map((o) => (o as HTMLOptionElement).value);
    expect(opts).toContain('oz');
  });

  it('reports the chosen unit through onChange', async () => {
    const onChange = vi.fn();
    const user = (await import('@testing-library/user-event')).default;
    render(<UnitSelect value="g" onChange={onChange} />);
    await user.selectOptions(screen.getByRole('combobox'), 'kg');
    expect(onChange).toHaveBeenCalledWith('kg');
  });
});
