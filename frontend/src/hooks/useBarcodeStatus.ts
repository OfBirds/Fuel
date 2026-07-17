import { useEffect, useState } from 'react';
import { apiFetch } from '../lib/api';

export interface BarcodeStatus {
  barcodeEnabled: boolean;
  barcodeStatusKnown: boolean;
}

// Barcode capability off GET /api/barcode/status — separate from useAiStatus, which only
// covers /api/ai/status. `barcodeStatusKnown` flips true once the fetch settles so callers
// can defer picking a starting input tab until every capability is known.
export function useBarcodeStatus(): BarcodeStatus {
  const [barcodeEnabled, setBarcodeEnabled] = useState(false);
  const [barcodeStatusKnown, setBarcodeStatusKnown] = useState(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      let on = false;
      try {
        const res = await apiFetch('/api/barcode/status');
        if (res.ok) on = (await res.json()).enabled === true;
      } catch { /* leave barcode off */ }
      if (!alive) return;
      setBarcodeEnabled(on);
      setBarcodeStatusKnown(true);
    })();
    return () => { alive = false; };
  }, []);

  return { barcodeEnabled, barcodeStatusKnown };
}
