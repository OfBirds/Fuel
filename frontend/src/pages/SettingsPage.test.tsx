import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('../context/AuthContext', () => {
  // Stable user reference so SettingsPage's useEffect([user]) doesn't re-fire each render.
  const user = { id: 'u1', email: 't@e.com' };
  return {
    useAuth: () => ({ user, token: 'x' }),
    AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  };
});

import SettingsPage from './SettingsPage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

describe('SettingsPage daily calorie goal', () => {
  beforeEach(() => vi.clearAllMocks());

  it('accepts a full multi-digit number and saves once, on blur', async () => {
    // initial prefs load
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({ notifyReleases: true, dailyCalorieGoal: null }),
    });

    const user = userEvent.setup();
    render(<SettingsPage />);

    const input = (await screen.findByRole('spinbutton')) as HTMLInputElement;
    await waitFor(() => expect(input).not.toBeDisabled());

    // the PUT that should fire on blur
    mockFetch.mockResolvedValueOnce({
      ok: true,
      json: async () => ({ notifyReleases: true, dailyCalorieGoal: 2000 }),
    });

    await user.type(input, '2000');

    // never disabled mid-typing → all four digits land, and nothing saved yet
    expect(input.value).toBe('2000');
    expect(mockFetch).toHaveBeenCalledTimes(1);

    await user.tab(); // blur

    await waitFor(() => expect(mockFetch).toHaveBeenCalledTimes(2));
    const [, putOpts] = mockFetch.mock.calls[1];
    expect(putOpts.method).toBe('PUT');
    expect(JSON.parse(putOpts.body).dailyCalorieGoal).toBe(2000);
  });
});
