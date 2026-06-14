import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

const register = vi.fn();
const login = vi.fn();

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: null, token: null, login, register, logout: vi.fn() }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

import LoginPage from './LoginPage';

describe('LoginPage password requirements', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows the live checklist only in the register view and tracks rule state', async () => {
    const user = userEvent.setup();
    render(<LoginPage onLoginSuccess={() => {}} />);

    // Not shown on the login view.
    expect(screen.queryByText('One special character')).toBeNull();

    // Switch to register (the toggle button — submit still reads "Sign In" here).
    await user.click(screen.getByRole('button', { name: 'Create Account' }));

    const rule = () => screen.getByText('One special character').closest('li')!;
    const lengthRule = () => screen.getByText('At least 8 characters').closest('li')!;
    expect(rule().className).not.toContain('met');

    await user.type(screen.getByLabelText('Password'), 'abc');
    expect(lengthRule().className).not.toContain('met');
    expect(rule().className).not.toContain('met');

    await user.clear(screen.getByLabelText('Password'));
    await user.type(screen.getByLabelText('Password'), 'Str0ng!pw');
    expect(lengthRule().className).toContain('met');
    expect(rule().className).toContain('met');
  });
});
