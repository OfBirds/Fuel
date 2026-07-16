import { apiFetch } from './api';

export interface CatalogueFood {
  id: string;
  name: string;
  defaultUoM: string;
  caloriesPerUnit: number;
  // Per-user usage count — only populated when the list endpoint gets a userId
  // (FoodController.cs GetFoods); used to infer the user's preferred unit system.
  usageCount: number | null;
}

// Fetch the global catalogue as a case-insensitive name → food map. Used to warn
// when someone is about to define a food that already exists (we dedup for real
// later; for now it's just a heads-up so we stop minting "chicken breast" twice).
// Pass userId to also get each food's usageCount (needed for unit-system inference).
export async function loadCatalogueByName(userId?: string): Promise<Map<string, CatalogueFood>> {
  const map = new Map<string, CatalogueFood>();
  try {
    const url = userId ? `/api/foods?userId=${encodeURIComponent(userId)}` : '/api/foods';
    const res = await apiFetch(url);
    if (!res.ok) return map;
    const foods = (await res.json()) as CatalogueFood[];
    for (const f of foods) map.set(f.name.trim().toLowerCase(), f);
  } catch { /* leave the map empty — duplicate detection just goes quiet */ }
  return map;
}
