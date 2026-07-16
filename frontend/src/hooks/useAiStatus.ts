import { useEffect, useState } from 'react';
import { apiFetch } from '../lib/api';

export interface AiStatus {
  aiEnabled: boolean | null;
  supportsText: boolean;
  supportsImages: boolean;
}

// AI estimation capability flags off GET /api/ai/status, shared by every screen that
// gates text/photo AI affordances (AiEntryPage, EntryFormPage's "Use AI" link). `aiEnabled`
// starts null (unknown) until the fetch resolves — callers that need a tri-state (e.g. to
// avoid flashing a "disabled" notice before the first response) can check `=== false`.
export function useAiStatus(): AiStatus {
  const [aiEnabled, setAiEnabled] = useState<boolean | null>(null);
  const [supportsText, setSupportsText] = useState(false);
  const [supportsImages, setSupportsImages] = useState(false);

  useEffect(() => {
    let alive = true;
    (async () => {
      let aiOn = false, sText = false, sImg = false;
      try {
        const res = await apiFetch('/api/ai/status');
        if (res.ok) {
          const s = await res.json();
          aiOn = s.enabled === true;
          sText = s.supportsText === true;
          sImg = s.supportsImages === true;
        }
      } catch { /* leave AI off */ }
      if (!alive) return;
      setAiEnabled(aiOn);
      setSupportsText(sText);
      setSupportsImages(sImg);
    })();
    return () => { alive = false; };
  }, []);

  return { aiEnabled, supportsText, supportsImages };
}
