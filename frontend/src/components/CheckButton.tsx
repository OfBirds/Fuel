// A reusable icon-only confirm button. Used wherever an action commits an estimate or a
// lookup (camera capture, barcode look-up, AI estimate) so they share one checkmark
// affordance instead of bespoke "OK"/"Capture"/"Look up" labels. Always pass a `label`
// (used as aria-label + tooltip) since there's no visible text. When `busy`, shows the
// shared spinner instead of the checkmark.

function CheckIcon() {
  return (
    <svg width="34" height="34" viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <path
        d="M5 13l4 4L19 7"
        stroke="currentColor"
        strokeWidth="2.5"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

type CheckButtonProps = React.ButtonHTMLAttributes<HTMLButtonElement> & {
  label: string;
  busy?: boolean;
};

export function CheckButton({ label, busy, ...props }: CheckButtonProps) {
  return (
    <button type="button" className="check-btn" aria-label={label} title={label} {...props}>
      {busy ? <span className="ai-spinner" aria-hidden="true" /> : <CheckIcon />}
    </button>
  );
}
