import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: { id: 'test-user-id', email: 'test@example.com' }, token: 'fake' }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

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
});
