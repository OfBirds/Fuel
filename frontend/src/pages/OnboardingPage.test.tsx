import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../context/AuthContext', () => {
  const user = { id: 'u1', email: 't@e.com' };
  return {
    useAuth: () => ({ user, token: 'x' }),
    AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  };
});

import OnboardingPage from './OnboardingPage';

const mockFetch = vi.fn();
globalThis.fetch = mockFetch;

describe('OnboardingPage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('calls onComplete after a successful save (lifts the gate without a manual refresh)', async () => {
    mockFetch.mockResolvedValue({ ok: true, json: async () => ({}) });
    const onComplete = vi.fn();
    const user = userEvent.setup();

    render(
      <MemoryRouter>
        <OnboardingPage onComplete={onComplete} />
      </MemoryRouter>
    );

    await user.type(screen.getByPlaceholderText('e.g. 180'), '180');   // height
    await user.type(screen.getByPlaceholderText('e.g. 1996'), '1996'); // year of birth
    await user.type(screen.getByPlaceholderText('e.g. 80'), '80');     // starting weight

    const selects = screen.getAllByRole('combobox');
    await user.selectOptions(selects[0], 'Male');   // Sex
    await user.selectOptions(selects[1], 'Medium'); // Body frame

    await user.click(screen.getByRole('button', { name: 'Get Started' }));

    await waitFor(() => expect(onComplete).toHaveBeenCalledTimes(1));
  });
});
