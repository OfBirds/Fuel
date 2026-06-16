import { apiFetch } from './api';

export interface CatalogueFood {
  id: string;
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
}

// Fetch the global catalogue as a case-insensitive name → food map. Used to warn
// when someone is about to define a food that already exists (we dedup for real
// later; for now it's just a heads-up so we stop minting "chicken breast" twice).
export async function loadCatalogueByName(): Promise<Map<string, CatalogueFood>> {
  const map = new Map<string, CatalogueFood>();
  try {
    const res = await apiFetch('/api/foods');
    if (!res.ok) return map;
    const foods = (await res.json()) as CatalogueFood[];
    for (const f of foods) map.set(f.name.trim().toLowerCase(), f);
  } catch { /* leave the map empty — duplicate detection just goes quiet */ }
  return map;
}
