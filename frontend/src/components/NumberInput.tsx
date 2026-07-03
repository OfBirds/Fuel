import { useEffect, useState } from 'react';

type NumberInputProps = Omit<
  React.InputHTMLAttributes<HTMLInputElement>,
  'value' | 'onChange' | 'type'
> & {
  value: number;
  onValueChange: (value: number) => void;
  /** What to emit when the field is left blank. Defaults to 0. */
  emptyValue?: number;
};

/**
 * Controlled number input that keeps its own string draft so the field can be cleared and
 * edited freely. Fixes the "leading 0 you can't delete" bug: previously an empty string was
 * coerced straight to a number on every keystroke, so a value of 0 re-appeared the instant
 * you tried to clear it. Here a blank field stays blank (emitting `emptyValue`), while still
 * re-syncing when the value changes from the outside (e.g. a computed calorie total).
 */
export function NumberInput({ value, onValueChange, emptyValue = 0, ...rest }: NumberInputProps) {
  const [draft, setDraft] = useState(() => String(value));

  // Re-sync only when the value changes externally — never clobber the draft the user is
  // typing. A blank draft counts as `emptyValue`, so we don't shove a "0" back into a field
  // that was just cleared.
  useEffect(() => {
    const current = draft === '' ? emptyValue : Number(draft);
    if (value !== current) {
      setDraft(String(value));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  return (
    <input
      {...rest}
      type="number"
      value={draft}
      onChange={(e) => {
        const next = e.target.value;
        setDraft(next);
        if (next === '') {
          onValueChange(emptyValue);
        } else {
          const parsed = Number(next);
          if (!Number.isNaN(parsed)) onValueChange(parsed);
        }
      }}
    />
  );
}
