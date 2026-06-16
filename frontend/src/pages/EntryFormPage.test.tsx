import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: { id: 'test-user-id', email: 'test@example.com' }, token: 'fake' }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

import EntryFormPage from './EntryFormPage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

function renderEntryForm(route: string = '/entry/new?meal=Lunch&date=2026-06-14') {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <Routes>
        <Route path="/entry/new" element={<EntryFormPage />} />
        <Route path="/" element={<div>Home</div>} />
      </Routes>
    </MemoryRouter>
  );
}

function mockMealPauseNotConfigured() {
  return { ok: true, json: async () => ({ isWithinPause: false, hoursSinceLast: null, mealPauseHours: null }) };
}

// EntryFormPage checks AI availability first on mount; default it to off.
function mockAiStatus(enabled = false) {
  return { ok: true, json: async () => ({ enabled, supportsImages: false }) };
}
describe('EntryFormPage', () => {
  beforeEach(() => {
    // resetAllMocks (not clearAllMocks) so each test starts with an empty
    // mockResolvedValueOnce queue — a prior test's debounced fetch may not have
    // consumed its queued response, which would shift this test's mock sequence.
    vi.resetAllMocks();
  });

  it('renders the add entry form', async () => {
    mockFetch.mockResolvedValueOnce(mockAiStatus()).mockResolvedValueOnce(mockMealPauseNotConfigured());
    renderEntryForm();
    await waitFor(() => {
      const headings = screen.getAllByText('Add Entry');
      expect(headings.length).toBeGreaterThan(0);
    });
  });

  it('searches foods on input', async () => {
    mockFetch
      // Default: any food-search call (focus shows all, then typing narrows) returns this.
      .mockResolvedValue({
        ok: true,
        json: async () => [
          { id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65, ingredientCount: 0, isComposite: false },
        ],
      });
    // Mount calls consumed first, in order.
    mockFetch
      .mockResolvedValueOnce(mockAiStatus())                // ai status (mount)
      .mockResolvedValueOnce(mockMealPauseNotConfigured());  // meal-pause check

    renderEntryForm();

    // Wait for mount fetches to fire before typing.
    await waitFor(() => {
      expect(mockFetch).toHaveBeenCalled();
    });
    // Give the meal-pause debounce (300ms) time to fire and consume its mock.
    await new Promise(r => setTimeout(r, 350));

    const inputs = screen.getAllByPlaceholderText('Search foods, or tap to see all…');
    await userEvent.type(inputs[0], 'chicken');

    await waitFor(() => {
      const results = screen.getAllByText('Chicken Breast');
      expect(results.length).toBeGreaterThan(0);
    }, { timeout: 3000 });
  });

  it('shows inline food define link', async () => {
    mockFetch.mockResolvedValueOnce(mockAiStatus()).mockResolvedValueOnce(mockMealPauseNotConfigured());
    renderEntryForm();
    await waitFor(() => {
      const links = screen.getAllByText("Can't find it? Define a new food");
      expect(links.length).toBeGreaterThan(0);
    });
  });

  it('does not show save button without food selected', async () => {
    mockFetch.mockResolvedValueOnce(mockAiStatus()).mockResolvedValueOnce(mockMealPauseNotConfigured());
    renderEntryForm();
    await waitFor(() => {
      expect(screen.queryByText('Save Entry')).toBeNull();
    });
  });
});
