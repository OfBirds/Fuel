import { useState, useEffect, useMemo, useCallback } from 'react';
import { useAiStatus } from '../hooks/useAiStatus';
import { useBarcodeStatus } from '../hooks/useBarcodeStatus';
import { loadCatalogueByName, type CatalogueFood } from '../lib/foods';
import { inferPreferredSystem, refQty } from '../lib/units';
import {
  AiInputPanel,
  type EstimateItem,
  type BarcodeFood,
} from './AiInputPanel';
import type { FoodFormData } from '../pages/CataloguePage';
import '../styles/aientry.css';

interface FoodAiAssistProps {
  userId: string;
  onApply: (prefill: Partial<FoodFormData> & { matchedFoodId: string | null }) => void;
}

const round2 = (n: number) => Math.round(n * 100) / 100;

// The AI/barcode assist inside the catalogue's ADD-food dialog. The whole top control
// (tabs, photo, barcode, estimate/refine) is the shared AiInputPanel — identical to the
// diary's AI entry screen. An estimate or barcode hit prefills the food form below
// directly (converted to reference-basis per-unit values); the dialog's own Save Food
// button is the only confirmation.
export function FoodAiAssist({ userId, onApply }: FoodAiAssistProps) {
  const { aiEnabled, supportsText, supportsImages } = useAiStatus();
  const { barcodeEnabled, barcodeStatusKnown } = useBarcodeStatus();

  const [emptyResult, setEmptyResult] = useState(false);

  // Catalogue + unit-system inference for row normalisation
  // (docs/food-catalogue-and-logging.md §Unit-system inference).
  const [catalogue, setCatalogue] = useState<Map<string, CatalogueFood>>(new Map());
  const preferredSystem = useMemo(
    () => inferPreferredSystem(Array.from(catalogue.values())),
    [catalogue],
  );

  useEffect(() => {
    if (!aiEnabled) return;
    let alive = true;
    loadCatalogueByName(userId).then((m) => { if (alive) setCatalogue(m); });
    return () => { alive = false; };
  }, [userId, aiEnabled]);

  // ── Prefill: convert absolute-for-quantity values to reference-basis per-unit ──
  // displayCalories = round(row.calories / row.quantity * refQty(row.uom), 2dp) — the
  // reference basis from docs/food-catalogue-and-logging.md §reference quantities.
  // Same for protein/carbs/fat, guarded for null. A description names ONE food, so the
  // first item is the food; the form fields below are the review surface.
  const handleResult = useCallback((items: EstimateItem[]) => {
    const row = items[0];
    setEmptyResult(!row);
    if (!row) return;
    const rq = refQty(row.uom);
    const qty = row.quantity > 0 ? row.quantity : 1;
    const toPerRef = (v: number | null) =>
      v == null ? undefined : round2(v / qty * rq);
    onApply({
      name: row.name,
      defaultUoM: row.uom,
      caloriesPerUnit: toPerRef(row.calories) ?? 0,
      proteinPerUnit: toPerRef(row.protein),
      carbsPerUnit: toPerRef(row.carbs),
      fatPerUnit: toPerRef(row.fat),
      matchedFoodId: row.matchedFoodId,
    });
  }, [onApply]);

  const handleReset = useCallback(() => setEmptyResult(false), []);

  // A barcode hit is already a catalogue food with per-1-unit values — prefill the form
  // directly (scaled to the reference basis). matchedFoodId triggers the duplicate hint.
  const handleBarcodeFood = useCallback((food: BarcodeFood) => {
    const rq = refQty(food.defaultUoM);
    const perRef = (v: number | null) => (v == null ? undefined : round2(v * rq));
    onApply({
      name: food.name,
      defaultUoM: food.defaultUoM,
      caloriesPerUnit: round2(food.caloriesPerUnit * rq),
      proteinPerUnit: perRef(food.proteinPerUnit),
      carbsPerUnit: perRef(food.carbsPerUnit),
      fatPerUnit: perRef(food.fatPerUnit),
      matchedFoodId: food.id,
    });
  }, [onApply]);

  // ── Gating ──
  // null / not-yet-known (loading) → render nothing (avoid flash of disabled UI)
  // nothing enabled → render nothing (assist not configured on this server)
  if (aiEnabled === null || !barcodeStatusKnown) return null;
  if (!aiEnabled && !barcodeEnabled) return null;

  return (
    <div className="food-ai-assist">
      <AiInputPanel
        userId={userId}
        kind="food"
        supportsText={aiEnabled && supportsText}
        supportsImages={aiEnabled && supportsImages}
        barcodeEnabled={barcodeEnabled}
        preferredSystem={preferredSystem}
        onResult={handleResult}
        onBarcodeFood={handleBarcodeFood}
        onReset={handleReset}
      />
      {emptyResult && (
        <p className="settings-muted">No food recognised — try refining with a note.</p>
      )}
    </div>
  );
}
