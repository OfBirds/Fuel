// Normalize a user-supplied photo before it goes to the vision estimator.
//
// Why: phones hand us HEIC (iOS photo library) and multi-megabyte images. The
// Anthropic Messages API rejects HEIC outright and 400s on images past its
// per-image size limit, so a raw upload fails even though the same model
// estimates text fine. Re-encoding to JPEG and capping the longest edge gives a
// universally-supported, safely-sized payload (and cuts upload time + token cost).
//
// We decode through an <img> (not createImageBitmap) because Safari can display
// HEIC that way — the same path the preview already uses — and browsers apply
// EXIF orientation when drawing an <img> to a canvas. If anything fails we return
// the original blob so the estimate can still be attempted.

const MAX_EDGE = 1568; // Anthropic's recommended max edge — larger is downscaled anyway
const QUALITY = 0.85;

function loadImage(blob: Blob): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const url = URL.createObjectURL(blob);
    const img = new Image();
    img.onload = () => { URL.revokeObjectURL(url); resolve(img); };
    img.onerror = () => { URL.revokeObjectURL(url); reject(new Error('image decode failed')); };
    img.src = url;
  });
}

export async function normalizeImage(input: Blob): Promise<Blob> {
  let img: HTMLImageElement;
  try {
    img = await loadImage(input);
  } catch {
    return input; // can't decode here — let the server/provider try the original
  }

  const longest = Math.max(img.naturalWidth, img.naturalHeight);
  const scale = longest > MAX_EDGE ? MAX_EDGE / longest : 1;
  const w = Math.max(1, Math.round(img.naturalWidth * scale));
  const h = Math.max(1, Math.round(img.naturalHeight * scale));

  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) return input;
  ctx.drawImage(img, 0, 0, w, h);

  const out = await new Promise<Blob | null>((resolve) =>
    canvas.toBlob((b) => resolve(b), 'image/jpeg', QUALITY),
  );
  return out ?? input;
}
