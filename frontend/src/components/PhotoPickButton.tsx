import { useRef, useState } from 'react';

interface PhotoPickButtonProps {
  label: string;
  onFile: (file: File) => void;
  disabled?: boolean;
}

// A single visible photo button that can't rely on the OS's file-input chooser to offer
// both camera and library — that chooser is not guaranteed across browsers/Android skins/
// installed-PWA contexts (see docs/OFB-40 design §3a-R1). Instead we own the choice: tapping
// the button opens a small in-app action sheet with two explicit actions, each wired to its
// own hidden file input — one forces the live camera (`capture="environment"`), the other
// always opens the picker/gallery (no `capture`).
export function PhotoPickButton({ label, onFile, disabled }: PhotoPickButtonProps) {
  const [open, setOpen] = useState(false);
  const cameraInputRef = useRef<HTMLInputElement>(null);
  const libraryInputRef = useRef<HTMLInputElement>(null);

  const handleFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ''; // allow re-selecting the same file
    setOpen(false);
    if (file) onFile(file);
  };

  return (
    <div className="photo-pick">
      <button
        type="button"
        className="save-btn ai-upload-btn"
        onClick={() => setOpen((o) => !o)}
        disabled={disabled}
      >
        {label}
      </button>

      {open && (
        <div className="photo-pick-sheet" role="menu">
          <button
            type="button"
            role="menuitem"
            className="photo-pick-action"
            onClick={() => cameraInputRef.current?.click()}
          >
            📷 Take photo
          </button>
          <button
            type="button"
            role="menuitem"
            className="photo-pick-action"
            onClick={() => libraryInputRef.current?.click()}
          >
            🖼 Choose from files
          </button>
        </div>
      )}

      <input
        ref={cameraInputRef}
        type="file"
        accept="image/*"
        capture="environment"
        hidden
        aria-label={`${label} — camera`}
        onChange={handleFile}
      />
      <input
        ref={libraryInputRef}
        type="file"
        accept="image/*"
        hidden
        aria-label={`${label} — files`}
        onChange={handleFile}
      />
    </div>
  );
}
