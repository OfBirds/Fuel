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

    expect(screen.getByText(/Protein \//)).toBeInTheDocument();
    expect(screen.getByText(/Carbs \//)).toBeInTheDocument();
    expect(screen.getByText(/Fat \//)).toBeInTheDocument();
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
});
