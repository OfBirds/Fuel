import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

const register = vi.fn();
const login = vi.fn();
const loginWithSSO = vi.fn().mockResolvedValue(undefined);
// Mutable auth stub (vi.hoisted-free: plain module vars the mock factory closes over).
let ssoOnline = false;
let ssoConfigured = false;
let authReady = true;

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    user: null, token: null, login, register, loginWithSSO,
    ssoOnline, ssoConfigured, authReady, logout: vi.fn(),
  }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
  SSO_BLOCKED_KEY: 'sso_blocked',
}));

import LoginPage from './LoginPage';

describe('LoginPage password requirements', () => {
  beforeEach(() => { vi.clearAllMocks(); ssoOnline = false; ssoConfigured = false; authReady = true; });

  it('shows the live checklist only in the register view and tracks rule state', async () => {
    const user = userEvent.setup();
    render(<LoginPage onLoginSuccess={() => {}} />);

    expect(screen.queryByText('One special character')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Create Account' }));

    const rule = () => screen.getByText('One special character').closest('li')!;
    const lengthRule = () => screen.getByText('At least 8 characters').closest('li')!;
    expect(rule().className).not.toContain('met');

    await user.type(screen.getByLabelText('Password'), 'abc');
    expect(lengthRule().className).not.toContain('met');

    await user.clear(screen.getByLabelText('Password'));
    await user.type(screen.getByLabelText('Password'), 'Str0ng!pw');
    expect(lengthRule().className).toContain('met');
    expect(rule().className).toContain('met');
  });
});

describe('LoginPage — CrimsonRaven-first', () => {
  beforeEach(() => { vi.clearAllMocks(); ssoOnline = false; ssoConfigured = false; authReady = true; window.location.hash = ''; localStorage.clear(); });

  it('redirects straight to CrimsonRaven when it is online — no form, no choice', async () => {
    ssoOnline = true;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.queryByLabelText('Email')).toBeNull();          // no legacy form
    await waitFor(() => expect(loginWithSSO).toHaveBeenCalledTimes(1)); // auto-redirect
  });

  it('falls back to the legacy form + maintenance notice when Raven is configured but down', () => {
    ssoConfigured = true; ssoOnline = false;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.getByText(/CrimsonRaven is offline/i)).toBeInTheDocument();
    expect(loginWithSSO).not.toHaveBeenCalled();
  });

  it('shows the plain legacy form (no notice) when SSO is not configured — local dev', () => {
    ssoConfigured = false; ssoOnline = false;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.queryByText(/CrimsonRaven is offline/i)).toBeNull();
  });

  it('held sign-in (unverified email): focused verify screen, no loop, password tucked away', async () => {
    const user = userEvent.setup();
    ssoConfigured = true; ssoOnline = true;                       // Raven up — would normally auto-redirect
    localStorage.setItem('sso_blocked', 'Please verify your email, then sign in again.');
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(loginWithSSO).not.toHaveBeenCalled();                  // the critical bit: NO redirect loop
    expect(screen.getByText(/Verify your email to finish signing in/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Log out \/ use a different account/i })).toBeInTheDocument();
    expect(screen.queryByLabelText('Email')).toBeNull();          // confusing legacy form hidden by default
    await user.click(screen.getByRole('button', { name: /Sign in with a password instead/i }));
    expect(screen.getByLabelText('Email')).toBeInTheDocument();   // revealed only on demand
  });
});
