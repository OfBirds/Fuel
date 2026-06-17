import { getShowMacros } from '../lib/storage';

// Whether macro (protein/carbs/fat) UI should be shown. The authoritative value is
// the server profile flag; it's mirrored into the local prefs cache wherever the
// profile/prefs are already fetched (Settings on load + toggle, HomePage on load) so
// the macro-bearing form pages can read it synchronously without a redundant fetch.
// Defaults to false (simple view) until that first sync, which is the safe default.
export function useShowMacros(): boolean {
  return getShowMacros();
}
