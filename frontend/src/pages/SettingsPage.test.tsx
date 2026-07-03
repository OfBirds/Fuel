import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('../context/AuthContext', () => {
  const user = { id: 'u1', email: 't@e.com' };
  return {
    useAuth: () => ({ user, token: 'x' }),
    AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  };
});

import SettingsPage from './SettingsPage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

const defaultPrefs = { notifyReleases: true, dailyCalorieGoal: null };
const defaultProfile = { height: null, sex: null, constitution: null, yearOfBirth: null, activityLevel: null, mealPauseHours: null, mealPauseScope: null, showMacros: false };

describe('SettingsPage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('accepts a full multi-digit number and saves once, on blur', async () => {
    // Mount: prefs + profile (Promise.all) + metabolism
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => defaultPrefs })
      .mockResolvedValueOnce({ ok: true, json: async () => defaultProfile })
      .mockResolvedValueOnce({ ok: false, json: async () => ({}) });

    const user = userEvent.setup();
    render(<SettingsPage />);

    // First spinbutton is the daily calorie goal
    const inputs = await screen.findAllByRole('spinbutton');
    const input = inputs[0] as HTMLInputElement;
    await waitFor(() => expect(input).not.toBeDisabled());

    // the PUT on blur
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({ notifyReleases: true, dailyCalorieGoal: 2000 }),
    });

    await user.type(input, '2000');
    expect(input.value).toBe('2000');

    await user.tab(); // blur

    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(4));
    const putCall = mockFetch.mock.calls[3];
    expect(putCall[1]!.method).toBe('PUT');
    expect(JSON.parse(putCall[1]!.body as string).dailyCalorieGoal).toBe(2000);
  });

  it('renders all setting sections', async () => {
    mockFetch
      .mockResolvedValueOnce({ ok: true, json: async () => defaultPrefs })
      .mockResolvedValueOnce({ ok: true, json: async () => defaultProfile })
      .mockResolvedValueOnce({ ok: false, json: async () => ({}) });

    render(<SettingsPage />);

    await waitFor(() => {
      expect(screen.getByText('Notifications')).toBeDefined();
      expect(screen.getByText('Daily Goal')).toBeDefined();
      expect(screen.getByText('Profile')).toBeDefined();
      expect(screen.getByText('Display')).toBeDefined();
      expect(screen.getByText('Meal Pause')).toBeDefined();
      expect(screen.getByText('Metabolism')).toBeDefined();
    });
  });
});
