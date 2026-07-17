import { useRef } from 'react';

interface PhotoPickButtonProps {
  label: string; // context prefix for the hidden inputs' aria-labels
  onFile: (file: File) => void;
  disabled?: boolean;
}

// Two direct photo actions side by side — no intermediate chooser. We can't rely on the
// OS's file-input dialog to offer both camera and library (not guaranteed across browsers/
// Android skins/installed-PWA contexts, see docs/ai-estimation.md §Capture), so each button
// is wired to its own hidden file input: "Take photo" forces the live camera
// (`capture="environment"`), "File upload" always opens the picker/gallery (no `capture`).
export function PhotoPickButton({ label, onFile, disabled }: PhotoPickButtonProps) {
  const cameraInputRef = useRef<HTMLInputElement>(null);
  const libraryInputRef = useRef<HTMLInputElement>(null);

  const handleFile = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ''; // allow re-selecting the same file
    if (file) onFile(file);
  };

  return (
    <div className="photo-pick">
      <button
        type="button"
        className="save-btn"
        onClick={() => cameraInputRef.current?.click()}
        disabled={disabled}
      >
        Take photo
      </button>
      <button
        type="button"
        className="save-btn"
        onClick={() => libraryInputRef.current?.click()}
        disabled={disabled}
      >
        File upload
      </button>

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
