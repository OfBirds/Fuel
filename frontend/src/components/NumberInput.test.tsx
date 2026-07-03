import { useState } from 'react';
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import { NumberInput } from './NumberInput';

describe('NumberInput', () => {
  it('emits parsed numbers as the user types', () => {
    const onValueChange = vi.fn();
    render(<NumberInput value={0} onValueChange={onValueChange} />);
    const input = screen.getByRole('spinbutton') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '85' } });
    expect(onValueChange).toHaveBeenLastCalledWith(85);
  });

  it('can be cleared without a stuck leading 0', () => {
    // The regression: clearing a 0-valued field used to snap back to "0" so you could never
    // delete the leading zero. The field must stay blank after clearing.
    function Host() {
      const [v, setV] = useState<number>(0);
      return <NumberInput value={v} onValueChange={(n) => setV(n ?? 0)} />;
    }
    render(<Host />);
    const input = screen.getByRole('spinbutton') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '' } });
    expect(input.value).toBe(''); // not "0"
    // and typing a fresh number does not keep any leading zero
    fireEvent.change(input, { target: { value: '7' } });
    expect(input.value).toBe('7');
  });

  it('reflects an externally changed value (e.g. a computed total)', () => {
    const { rerender } = render(<NumberInput value={100} onValueChange={() => {}} />);
    const input = screen.getByRole('spinbutton') as HTMLInputElement;
    expect(input.value).toBe('100');
    rerender(<NumberInput value={250} onValueChange={() => {}} />);
    expect(input.value).toBe('250');
  });

  it('emits emptyValue (0 by default) when blank', () => {
    const onValueChange = vi.fn();
    render(<NumberInput value={5} onValueChange={onValueChange} />);
    const input = screen.getByRole('spinbutton') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '' } });
    expect(onValueChange).toHaveBeenLastCalledWith(0);
  });

  it('honours a custom emptyValue', () => {
    const onValueChange = vi.fn();
    render(<NumberInput value={5} emptyValue={1} onValueChange={onValueChange} />);
    const input = screen.getByRole('spinbutton') as HTMLInputElement;
    fireEvent.change(input, { target: { value: '' } });
    expect(onValueChange).toHaveBeenLastCalledWith(1);
  });
});
