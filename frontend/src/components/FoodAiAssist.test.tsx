import { describe, it, expect, vi, beforeEach, afterEach, afterAll } from 'vitest';
import { render, screen, waitFor, cleanup } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

// Image normalization decodes via <img>/canvas, which jsdom can't do — pass through.
vi.mock('../lib/image', () => ({ normalizeImage: async (b: Blob) => b }));

import { FoodAiAssist } from './FoodAiAssist';

const _realFetch = globalThis.fetch;
const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

function renderPanel(onApply = vi.fn()) {
  return render(<FoodAiAssist userId="test-user-id" onApply={onApply} />);
}

const aiOn = () => ({ ok: true, json: async () => ({ enabled: true, supportsText: true, supportsImages: true }) });
const aiOff = () => ({ ok: true, json: async () => ({ enabled: false, supportsText: false, supportsImages: false }) });
const foodsEmpty = () => ({ ok: true, json: async () => [] });

function estimateOk(items: unknown[]) {
  return { ok: true, json: async () => ({ ok: true, error: null, overallConfidence: 0.7, source: 'AiText', items }) };
}

function estimateFail(error: string) {
  return { ok: true, json: async () => ({ ok: false, error, overallConfidence: 0, source: 'AiText', items: [] }) };
}

const row = (over: Record<string, unknown> = {}) => ({
  name: 'Chicken Breast', quantity: 200, uom: 'g', calories: 330,
  protein: 62, carbs: 0, fat: 7, confidence: 0.9,
  matchedFoodId: 'food-1', matchedDefaultUoM: 'g', isNew: false, ...over,
});

describe('FoodAiAssist', () => {
  beforeEach(() => vi.clearAllMocks());
  afterEach(() => cleanup());
  afterAll(() => { globalThis.fetch = _realFetch; });

  it('renders nothing when aiEnabled is false', async () => {
    mockFetch.mockResolvedValueOnce(aiOff());
    const { container } = renderPanel();
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(1));
    expect(container.querySelector('.food-ai-assist')).toBeNull();
  });

  it('renders nothing while aiEnabled is still loading (null)', () => {
    // Don't resolve the fetch — aiEnabled stays null
    mockFetch.mockImplementation(() => new Promise(() => {}));
    const { container } = renderPanel();
    expect(container.querySelector('.food-ai-assist')).toBeNull();
  });

  it('text estimate returning one item: Apply calls onApply with reference-basis values', async () => {
    // 200 g, 330 cal → per 100 g: 330/200*100 = 165
    mockFetch
      .mockResolvedValueOnce(aiOn())       // /api/ai/status
      .mockResolvedValueOnce(foodsEmpty())  // /api/foods?userId=... (catalogue)
      .mockResolvedValueOnce(estimateOk([row({ name: 'Chicken Breast', quantity: 200, uom: 'g', calories: 330, protein: 62, carbs: 0, fat: 7 })]));

    const onApply = vi.fn();
    renderPanel(onApply);

    await screen.findByRole('textbox', { name: /describe the food/i });
    await userEvent.type(screen.getByRole('textbox', { name: /describe the food/i }), 'chicken breast');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByText('Chicken Breast');
    await userEvent.click(screen.getByRole('button', { name: 'Apply Chicken Breast' }));

    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onApply).toHaveBeenCalledWith({
      name: 'Chicken Breast',
      defaultUoM: 'g',
      caloriesPerUnit: 165,       // 330/200*100
      proteinPerUnit: 31,         // 62/200*100
      carbsPerUnit: 0,            // 0/200*100
      fatPerUnit: 3.5,            // 7/200*100
      matchedFoodId: 'food-1',
    });
  });

  it('multi-item response: each row has its own Apply that only applies that row', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(foodsEmpty())
      .mockResolvedValueOnce(estimateOk([
        row({ name: 'Chicken Breast', quantity: 200, uom: 'g', calories: 330, matchedFoodId: 'food-1' }),
        row({ name: 'Broccoli', quantity: 150, uom: 'g', calories: 51, protein: 4, carbs: 9, fat: 0.5, matchedFoodId: 'food-2' }),
      ]));

    const onApply = vi.fn();
    renderPanel(onApply);

    await screen.findByRole('textbox', { name: /describe the food/i });
    await userEvent.type(screen.getByRole('textbox', { name: /describe the food/i }), 'chicken and broccoli');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByText('Broccoli');
    expect(screen.getByText('Chicken Breast')).toBeInTheDocument();

    // Click only Chicken Breast's Apply
    await userEvent.click(screen.getByRole('button', { name: 'Apply Chicken Breast' }));

    expect(onApply).toHaveBeenCalledTimes(1);
    // Should have Chicken Breast data, not Broccoli
    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      name: 'Chicken Breast',
      matchedFoodId: 'food-1',
    }));
  });

  it('ok: false response shows server error and does not call onApply', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(foodsEmpty())
      .mockResolvedValueOnce(estimateFail('Unrecognisable — try a clearer description.'));

    const onApply = vi.fn();
    renderPanel(onApply);

    await screen.findByRole('textbox', { name: /describe the food/i });
    await userEvent.type(screen.getByRole('textbox', { name: /describe the food/i }), 'xyzzy blurgh');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await waitFor(() => {
      expect(screen.getByRole('alert')).toHaveTextContent('Unrecognisable — try a clearer description.');
    });
    expect(onApply).not.toHaveBeenCalled();
    // No Apply buttons should exist since rows is null
    expect(screen.queryByRole('button', { name: /Apply/ })).toBeNull();
  });

  it('refine after a failed estimate: refine input is visible and re-issues estimate with notes', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(foodsEmpty())
      .mockResolvedValueOnce(estimateFail('Unclear — add detail.'))
      .mockResolvedValueOnce(estimateOk([row({ name: 'Chicken Thigh', quantity: 150, uom: 'g', calories: 270 })]));

    const onApply = vi.fn();
    renderPanel(onApply);

    await screen.findByRole('textbox', { name: /describe the food/i });
    await userEvent.type(screen.getByRole('textbox', { name: /describe the food/i }), 'chicken');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    // After failed estimate, refine input should be visible
    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument());
    expect(screen.getByLabelText(/Add a clarification/)).toBeInTheDocument();

    // Type a refinement and submit
    await userEvent.type(screen.getByLabelText(/Add a clarification/), "it's a thigh, with skin");
    await userEvent.click(screen.getByRole('button', { name: 'Refine' }));

    // Should have made a 4th fetch (the re-estimate)
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(4));
    const [, opts] = mockFetch.mock.calls[3];
    const body = JSON.parse((opts as RequestInit).body as string);
    expect(body.notes).toEqual(["it's a thigh, with skin"]);
  });

  it('shows refine box after a successful estimate too', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(foodsEmpty())
      .mockResolvedValueOnce(estimateOk([row()]));

    renderPanel();

    await screen.findByRole('textbox', { name: /describe the food/i });
    await userEvent.type(screen.getByRole('textbox', { name: /describe the food/i }), 'chicken breast');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByText('Chicken Breast');
    // Refine input should be visible after successful estimate too
    expect(screen.getByLabelText(/Add a clarification/)).toBeInTheDocument();
  });

  it('hides photo tab when supportsImages is false', async () => {
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => ({ enabled: true, supportsText: true, supportsImages: false }) })
      .mockResolvedValueOnce(foodsEmpty());

    renderPanel();

    // aiEnabled: true triggers loadCatalogueByName → 2 calls (ai-status + foods)
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(2));
    // No photo tab should exist, only text mode
    expect(screen.queryByRole('tab', { name: 'Photo' })).toBeNull();
    // Text mode still works
    expect(screen.getByRole('textbox', { name: /describe the food/i })).toBeInTheDocument();
  });

  it('shows mode toggle when both text and images are supported', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(foodsEmpty());

    renderPanel();

    // ai-status + catalogue load
    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(2));
    expect(screen.getByRole('tab', { name: 'Text' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Photo' })).toBeInTheDocument();
  });

  it('handles zero-quantity row gracefully in apply conversion', async () => {
    mockFetch
      .mockResolvedValueOnce(aiOn())
      .mockResolvedValueOnce(foodsEmpty())
      .mockResolvedValueOnce(estimateOk([row({ quantity: 0, uom: 'g', calories: 0, protein: 0, carbs: 0, fat: 0 })]));

    const onApply = vi.fn();
    renderPanel(onApply);

    await screen.findByRole('textbox', { name: /describe the food/i });
    await userEvent.type(screen.getByRole('textbox', { name: /describe the food/i }), 'nothing');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByRole('button', { name: 'Apply Chicken Breast' });
    await userEvent.click(screen.getByRole('button', { name: 'Apply Chicken Breast' }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({
      caloriesPerUnit: 0,
      proteinPerUnit: 0,
      carbsPerUnit: 0,
      fatPerUnit: 0,
    }));
  });
});
