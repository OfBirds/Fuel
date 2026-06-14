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

describe('EntryFormPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('renders the add entry form', async () => {
    renderEntryForm();
    await waitFor(() => {
      const headings = screen.getAllByText('Add Entry');
      expect(headings.length).toBeGreaterThan(0);
    });
  });

  it('searches foods on input', async () => {
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => [
        { id: '1', name: 'Chicken Breast', defaultUoM: 'g', caloriesPerUnit: 1.65, ingredientCount: 0, isComposite: false },
      ],
    });

    renderEntryForm();

    const inputs = screen.getAllByPlaceholderText('Type a food name…');
    await userEvent.type(inputs[0], 'chicken');

    await waitFor(() => {
      const results = screen.getAllByText('Chicken Breast');
      expect(results.length).toBeGreaterThan(0);
    }, { timeout: 3000 });
  });

  it('shows inline food define link', async () => {
    renderEntryForm();
    await waitFor(() => {
      const links = screen.getAllByText("Can't find it? Define a new food");
      expect(links.length).toBeGreaterThan(0);
    });
  });

  it('does not show save button without food selected', async () => {
    renderEntryForm();
    await waitFor(() => {
      expect(screen.queryByText('Save Entry')).toBeNull();
    });
  });
});
