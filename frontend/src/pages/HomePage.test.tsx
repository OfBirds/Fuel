import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: { id: 'test-user-id', email: 'test@example.com' }, token: 'fake' }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

import HomePage from './HomePage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

function renderHomePage() {
  return render(
    <MemoryRouter>
      <HomePage />
    </MemoryRouter>
  );
}

describe('HomePage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('shows empty state when no entries', async () => {
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => [] })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ notifyReleases: true, dailyCalorieGoal: 2000 }) });

    renderHomePage();
    await waitFor(() => {
      const el = screen.getAllByText('Nothing logged yet');
      expect(el.length).toBeGreaterThan(0);
    });
  });

  it('groups entries by meal type', async () => {
    mockFetch
      .mockResolvedValueOnce({
        ok: true,
        json: async () => [
          { id: '1', foodId: null, foodName: 'Eggs', intakeAtUtc: '2026-06-14T08:00:00Z', mealType: 'Breakfast', quantity: 2, uoM: 'piece', calories: 160, protein: 12, carbs: 2, fat: 10, source: 'Manual', aiConfidence: null },
          { id: '2', foodId: null, foodName: 'Chicken', intakeAtUtc: '2026-06-14T12:00:00Z', mealType: 'Lunch', quantity: 200, uoM: 'g', calories: 330, protein: 62, carbs: 0, fat: 7.2, source: 'Manual', aiConfidence: null },
        ],
      })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ notifyReleases: true, dailyCalorieGoal: 2000 }) });

    renderHomePage();
    await waitFor(() => {
      expect(screen.getAllByText('Eggs').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Chicken').length).toBeGreaterThan(0);
    });
  });

  it('shows total vs goal progress', async () => {
    const entries = [
      { id: '1', foodId: null, foodName: 'Eggs', intakeAtUtc: '2026-06-14T08:00:00Z', mealType: 'Breakfast', quantity: 2, uoM: 'piece', calories: 160, protein: 12, carbs: 2, fat: 10, source: 'Manual', aiConfidence: null },
      { id: '2', foodId: null, foodName: 'Chicken', intakeAtUtc: '2026-06-14T12:00:00Z', mealType: 'Lunch', quantity: 200, uoM: 'g', calories: 330, protein: 62, carbs: 0, fat: 7.2, source: 'Manual', aiConfidence: null },
      { id: '3', foodId: null, foodName: 'Rice', intakeAtUtc: '2026-06-14T18:00:00Z', mealType: 'Dinner', quantity: 150, uoM: 'g', calories: 195, protein: 4, carbs: 42, fat: 0.5, source: 'Manual', aiConfidence: null },
    ];
    const prefs = { notifyReleases: true, dailyCalorieGoal: 2000 };
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => entries })
      .mockResolvedValueOnce({ ok: true, json: async () => prefs })
      .mockResolvedValueOnce({ ok: true, json: async () => entries })
      .mockResolvedValueOnce({ ok: true, json: async () => prefs });

    renderHomePage();
    await waitFor(() => {
      // All three entries should be rendered at least once
      expect(screen.getAllByText('Eggs').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Chicken').length).toBeGreaterThan(0);
      expect(screen.getAllByText('Rice').length).toBeGreaterThan(0);
    }, { timeout: 3000 });
    // Calorie summary ring + figures: 685 consumed of 2000 → 1315 left, 34%.
    expect(screen.getByText('Cal cons: 685')).toBeInTheDocument();
    expect(screen.getByText('Cal left: 1315')).toBeInTheDocument();
    expect(screen.getByText('34%')).toBeInTheDocument();
  });

  it('shows add buttons for each meal section', async () => {
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => [] })
      .mockResolvedValueOnce({ ok: true, json: async () => ({ notifyReleases: true, dailyCalorieGoal: null }) });

    renderHomePage();
    await waitFor(() => {
      expect(screen.getAllByLabelText('Add Breakfast').length).toBeGreaterThan(0);
      expect(screen.getAllByLabelText('Add Lunch').length).toBeGreaterThan(0);
      expect(screen.getAllByLabelText('Add Dinner').length).toBeGreaterThan(0);
      expect(screen.getAllByLabelText('Add Snack').length).toBeGreaterThan(0);
    });
  });
});
