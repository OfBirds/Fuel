import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

const register = vi.fn();
const login = vi.fn();
const loginWithSSO = vi.fn();
let ssoEnabled = false;

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({ user: null, token: null, login, register, loginWithSSO, ssoEnabled, logout: vi.fn() }),
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

describe('LoginPage — dual login (CrimsonRaven SSO + local)', () => {
  beforeEach(() => { vi.clearAllMocks(); ssoEnabled = false; window.location.hash = ''; });

  it('shows the SSO button next to the email form and triggers the redirect', async () => {
    ssoEnabled = true;
    const user = userEvent.setup();
    render(<LoginPage onLoginSuccess={() => {}} />);

    expect(screen.getByLabelText('Email')).toBeInTheDocument(); // local form stays (dual)
    await user.click(screen.getByRole('button', { name: /continue with crimsonraven/i }));
    expect(loginWithSSO).toHaveBeenCalledTimes(1);
  });

  it('hides the SSO button when SSO is not configured for the stack', () => {
    ssoEnabled = false;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.queryByRole('button', { name: /continue with crimsonraven/i })).toBeNull();
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
  });
});
