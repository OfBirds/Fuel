import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: { id: 'test-user-id', email: 'test@example.com' }, token: 'fake' }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

import StatsPage from './StatsPage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

// Two distinct logged days → daily totals of 2000 and 1000. The summary is per-day, so
// avg = 1500, max = 2000, min = 1000 — independent of which week these fall in.
const ENTRIES = [
  { id: '1', intakeAtUtc: '2026-06-15T08:00:00Z', calories: 2000 },
  { id: '2', intakeAtUtc: '2026-06-16T08:00:00Z', calories: 1000 },
];

function mockApi(goal: number | null = 1800) {
  mockFetch.mockImplementation((url: string) => {
    if (url.includes('/prefs')) {
      return Promise.resolve({ ok: true, json: async () => ({ dailyCalorieGoal: goal }) });
    }
    return Promise.resolve({ ok: true, json: async () => ENTRIES });
  });
}

function renderStats() {
  return render(
    <MemoryRouter>
      <StatsPage />
    </MemoryRouter>,
  );
}

describe('StatsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('renders the range toggle, defaulting to Week', () => {
    mockApi();
    renderStats();
    expect(screen.getByRole('tab', { name: 'Week' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Month' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Year' })).toBeInTheDocument();
  });

  it('computes per-day avg / max / min against the daily goal', async () => {
    mockApi(1800);
    renderStats();

    await waitFor(() => {
      expect(screen.getByText('1500')).toBeInTheDocument(); // daily avg
    });
    expect(screen.getByText('2000')).toBeInTheDocument(); // max day
    expect(screen.getByText('1000')).toBeInTheDocument(); // min day
    // Goal repeated in all three rows.
    expect(screen.getAllByText('1800')).toHaveLength(3);
    // Rings: 1500/1800 = 83%, 2000/1800 = 111%, 1000/1800 = 56%.
    expect(screen.getByText('83%')).toBeInTheDocument();
    expect(screen.getByText('111%')).toBeInTheDocument();
    expect(screen.getByText('56%')).toBeInTheDocument();
  });

  it('shows a dash for the rings when no goal is set', async () => {
    mockApi(null);
    renderStats();
    await waitFor(() => {
      expect(screen.getByText('1500')).toBeInTheDocument();
    });
    // No goal → three rings show an em-dash, plus the three goal cells.
    expect(screen.getAllByText('—').length).toBeGreaterThanOrEqual(3);
  });

  it('switching range persists the choice and updates the active tab', async () => {
    mockApi();
    renderStats();
    const monthTab = screen.getByRole('tab', { name: 'Month' });
    await userEvent.click(monthTab);
    await waitFor(() => {
      expect(monthTab).toHaveAttribute('aria-selected', 'true');
    });
    expect(localStorage.getItem('app:statsRange')).toBe('"month"');
  });
});
