import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: { id: 'test-user-id', email: 'test@example.com' }, token: 'fake' }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

// FoodAiAssist is rendered inside the food-form dialog — prevent its
// async initialisation fetches from leaking into unrelated tests.
vi.mock('../hooks/useAiStatus', () => ({
  useAiStatus: () => ({ aiEnabled: true, supportsText: true, supportsImages: true }),
}));

vi.mock('../lib/foods', async () => {
  const actual = await vi.importActual<typeof import('../lib/foods')>('../lib/foods');
  return { ...actual, loadCatalogueByName: vi.fn().mockResolvedValue(new Map()) };
});

import CataloguePage from './CataloguePage';
import { saveShowMacros } from '../lib/storage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

function renderCatalogue() {
  return render(
    <MemoryRouter>
      <CataloguePage />
    </MemoryRouter>
  );
}

describe('CataloguePage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();

    // JSDOM HTMLDialogElement — ensure showModal/close toggle the open attribute
    HTMLDialogElement.prototype.showModal = function () {
      this.setAttribute('open', '');
    };
    HTMLDialogElement.prototype.close = function () {
      this.removeAttribute('open');
    };
  });

  it('lists foods from API', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => [
        { id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65, ingredientCount: 0, isComposite: false },
        { id: '2', name: 'Olive Oil', defaultUoM: 'g', caloriesPerUnit: 8.84, ingredientCount: 0, isComposite: false },
      ],
    });

    renderCatalogue();

    await waitFor(() => {
      expect(screen.getAllByText('Chicken Breast').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Olive Oil').length).toBeGreaterThan(0);
    });
  });

  it('shows add food form when clicking Add Food', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });

    renderCatalogue();
    await waitFor(() => {
      expect(screen.getByLabelText('Add food')).toBeDefined();
    });

    await userEvent.click(screen.getByLabelText('Add food'));

    expect(screen.getAllByText('Save Food').length).toBeGreaterThan(0);
  });

  it('hides macro fields in the food form when show-macros is off (default)', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });

    renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeDefined());
    await userEvent.click(screen.getByLabelText('Add food'));

    expect(screen.queryByText(/Protein \//)).toBeNull();
    expect(screen.queryByText(/Carbs \//)).toBeNull();
    expect(screen.queryByText(/Fat \//)).toBeNull();
  });

  it('shows macro fields in the food form when show-macros is on', async () => {
    saveShowMacros(true);
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });

    renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeDefined());
    await userEvent.click(screen.getByLabelText('Add food'));

    // With show-macros on, all three macro labels should appear
    expect(screen.getByText('Protein per 100 g (g)')).toBeInTheDocument();
    expect(screen.getByText('Carbs per 100 g (g)')).toBeInTheDocument();
    expect(screen.getByText('Fat per 100 g (g)')).toBeInTheDocument();
  });

  it('shows empty state when no foods', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });

    renderCatalogue();
    await waitFor(() => {
      expect(screen.getAllByText('No foods in the catalogue yet.').length).toBeGreaterThan(0);
    });
  });

  it('shows composite badge for foods with ingredients', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => [
        { id: '1', name: 'Chicken Salad', defaultUoM: 'g', caloriesPerUnit: 1.5, ingredientCount: 3, isComposite: true },
      ],
    });

    renderCatalogue();
    await waitFor(() => {
      expect(screen.getAllByText('composite').length).toBeGreaterThan(0);
    });
  });

  it('renders sort mode selector and changes order on selection', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });

    const { container } = renderCatalogue();
    await waitFor(() => {
      expect(screen.getAllByText('No foods in the catalogue yet.').length).toBeGreaterThan(0);
    });

    // Sort selector should be in the document
    const select = container.querySelector('.catalogue-sort') as HTMLSelectElement;
    expect(select).toBeInTheDocument();
    expect(select.value).toBe('priority');

    // Change sort mode
    await userEvent.selectOptions(select, 'alphabetical');
    expect(select.value).toBe('alphabetical');
  });

  it('shows a duplicate hint with an edit-existing action on 409 from save', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] }); // initial list
    renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeDefined());

    await userEvent.click(screen.getByLabelText('Add food'));
    await userEvent.type(screen.getByPlaceholderText('e.g. Chicken Breast'), 'Chicken Breast');

    mockFetch.mockResolvedValueOnce({
      ok: false,
      status: 409,
      json: async () => ({ error: 'A food named "Chicken Breast" already exists.', existingFoodId: 'existing-id' }),
    });

    await userEvent.click(screen.getByText('Save Food'));

    await waitFor(() => {
      expect(screen.getByText('A food named "Chicken Breast" already exists.')).toBeInTheDocument();
    });
    const editLink = screen.getByText('Edit the existing food instead');
    expect(editLink).toBeInTheDocument();

    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({
        id: 'existing-id', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65,
        ingredientCount: 0, isComposite: false, ponder: 100, usageCount: null, lastUsedAtUtc: null,
        proteinPerUnit: null, carbsPerUnit: null, fatPerUnit: null, ingredients: [],
      }),
    });

    await userEvent.click(editLink);

    await waitFor(() => {
      expect(screen.getByText('Edit Food')).toBeInTheDocument();
    });
    expect(mockFetch).toHaveBeenLastCalledWith('/api/foods/existing-id', expect.anything());
  });

  it('round-trips reference-basis calories through edit and save', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] }); // initial list
    renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeDefined());

    await userEvent.click(screen.getByLabelText('Add food'));
    await userEvent.type(screen.getByPlaceholderText('e.g. Chicken Breast'), 'Chicken Breast');

    // Save → 409 duplicate
    mockFetch.mockResolvedValueOnce({
      ok: false, status: 409,
      json: async () => ({ error: 'Already exists.', existingFoodId: 'existing-id' }),
    });
    await userEvent.click(screen.getByText('Save Food'));

    await waitFor(() => {
      expect(screen.getByText('Edit the existing food instead')).toBeInTheDocument();
    });

    // Load food detail for editing (startEdit)
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({
        id: 'existing-id', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65,
        ingredientCount: 0, isComposite: false, ponder: 100, usageCount: null, lastUsedAtUtc: null,
        proteinPerUnit: null, carbsPerUnit: null, fatPerUnit: null, ingredients: [],
      }),
    });
    await userEvent.click(screen.getByText('Edit the existing food instead'));

    await waitFor(() => {
      expect(screen.getByText('Edit Food')).toBeInTheDocument();
    });

    // The Calories field should show the reference-basis value (1.65 × 100 = 165)
    expect(screen.getByRole('spinbutton')).toHaveValue(165);

    // Save the edit → success
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => ({}) });
    // Refetch on form close after save
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });

    await userEvent.click(screen.getByText('Save Food'));

    await waitFor(() => {
      expect(screen.queryByText('Edit Food')).toBeNull();
    });

    // Verify the PUT body has per-1-unit value (165 ÷ 100 = 1.65)
    const putCall = mockFetch.mock.calls.find(
      ([url, opts]) => url === '/api/foods/existing-id' && opts?.method === 'PUT'
    );
    expect(putCall).toBeDefined();
    const body = JSON.parse(putCall![1].body as string);
    expect(body.caloriesPerUnit).toBe(1.65);
  });

  it('displays reference-basis string in food card', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => [
        { id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65, ingredientCount: 0, isComposite: false },
        { id: '2', name: 'Chicken Salad', defaultUoM: 'g', caloriesPerUnit: 1.5, ingredientCount: 3, isComposite: true },
      ],
    });

    renderCatalogue();

    await waitFor(() => {
      expect(screen.getAllByText(/cal\/100 g/).length).toBeGreaterThanOrEqual(1);
    });
  });

  it('opens the edit dialog when clicking a food card row', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => [
        { id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65, ingredientCount: 0, isComposite: false },
      ],
    });
    renderCatalogue();
    await waitFor(() => expect(screen.getByText('Chicken Breast')).toBeInTheDocument());

    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({
        id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65,
        proteinPerUnit: null, carbsPerUnit: null, fatPerUnit: null, ingredients: [],
      }),
    });

    await userEvent.click(screen.getByText('Chicken Breast'));

    await waitFor(() => {
      expect(screen.getByText('Edit Food')).toBeInTheDocument();
    });
    expect(screen.getByRole('spinbutton')).toHaveValue(165);
  });

  it('opens the edit dialog when clicking the ✎ edit button', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => [
        { id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65, ingredientCount: 0, isComposite: false },
      ],
    });
    renderCatalogue();
    await waitFor(() => expect(screen.getByText('Chicken Breast')).toBeInTheDocument());

    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({
        id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65,
        proteinPerUnit: null, carbsPerUnit: null, fatPerUnit: null, ingredients: [],
      }),
    });

    await userEvent.click(screen.getByLabelText('Edit Chicken Breast'));

    await waitFor(() => {
      expect(screen.getByText('Edit Food')).toBeInTheDocument();
    });
    expect(screen.getByRole('spinbutton')).toHaveValue(165);
  });

  it('closes the dialog on Cancel without extra fetch', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] });
    const { container } = renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeInTheDocument());

    await userEvent.click(screen.getByLabelText('Add food'));
    await waitFor(() => expect(screen.getByText('Save Food')).toBeInTheDocument());

    const callCount = mockFetch.mock.calls.length;
    await userEvent.click(screen.getByText('Cancel'));

    // After Cancel, the dialog's open attribute should be gone
    const dialog = container.querySelector<HTMLDialogElement>('.food-form-dialog')!;
    await waitFor(() => {
      expect(dialog.hasAttribute('open')).toBe(false);
    });

    // Cancel should not trigger any additional fetch
    expect(mockFetch.mock.calls.length).toBe(callCount);
  });

  it('applying from the AI panel prefills the visible form fields', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] }); // initial list
    renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeDefined());

    await userEvent.click(screen.getByLabelText('Add food'));
    await waitFor(() => expect(screen.getByText('Save Food')).toBeInTheDocument());

    // Ensure the AI panel is rendered inside the dialog
    expect(screen.getByLabelText('Describe the food')).toBeInTheDocument();

    // Mock the estimate response: 200g chicken @ 330cal
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({
        ok: true, error: null, overallConfidence: 0.9, source: 'AiText',
        items: [{
          name: 'Chicken Breast', quantity: 200, uom: 'g', calories: 330,
          protein: 62, carbs: 0, fat: 7, confidence: 0.9,
          matchedFoodId: 'food-1', matchedDefaultUoM: 'g', isNew: false,
        }],
      }),
    });

    await userEvent.type(screen.getByLabelText('Describe the food'), 'chicken breast');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    // Wait for the result row and click Apply
    await screen.findByText('Chicken Breast');
    await userEvent.click(screen.getByRole('button', { name: 'Apply Chicken Breast' }));

    // Form fields should be pre-filled with reference-basis values
    // 330/200*100 = 165 cal per 100g
    await waitFor(() => {
      const nameInput = screen.getByPlaceholderText('e.g. Chicken Breast') as HTMLInputElement;
      expect(nameInput.value).toBe('Chicken Breast');
    });
    expect(screen.getByRole('spinbutton')).toHaveValue(165);
  });

  it('applying with a matchedFoodId in add mode shows the duplicate hint', async () => {
    mockFetch.mockResolvedValueOnce({ ok: true, json: async () => [] }); // initial list
    renderCatalogue();
    await waitFor(() => expect(screen.getByLabelText('Add food')).toBeDefined());

    await userEvent.click(screen.getByLabelText('Add food'));
    await waitFor(() => expect(screen.getByText('Save Food')).toBeInTheDocument());

    // Mock estimate with a matchedFoodId
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({
        ok: true, error: null, overallConfidence: 0.9, source: 'AiText',
        items: [{
          name: 'Chicken Breast', quantity: 200, uom: 'g', calories: 330,
          protein: 62, carbs: 0, fat: 7, confidence: 0.9,
          matchedFoodId: 'existing-food-id', matchedDefaultUoM: 'g', isNew: false,
        }],
      }),
    });

    await userEvent.type(screen.getByLabelText('Describe the food'), 'chicken');
    await userEvent.click(screen.getByRole('button', { name: 'Estimate' }));

    await screen.findByText('Chicken Breast');
    await userEvent.click(screen.getByRole('button', { name: 'Apply Chicken Breast' }));

    // Duplicate hint should appear with the edit link
    await waitFor(() => {
      expect(screen.getByText('Edit the existing food instead')).toBeInTheDocument();
    });
  });
});
