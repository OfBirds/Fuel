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
let needsInteractiveLogin = false;

vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    user: null, token: null, login, register, loginWithSSO,
    ssoOnline, ssoConfigured, authReady, needsInteractiveLogin, logout: vi.fn(),
  }),
  AuthProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
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
  beforeEach(() => { vi.clearAllMocks(); ssoOnline = false; ssoConfigured = false; authReady = true; needsInteractiveLogin = false; window.location.hash = ''; localStorage.clear(); });

  it('shows a spinner (never the form) while the engine silently probes SSO — no auto-redirect', async () => {
    ssoOnline = true; needsInteractiveLogin = false; // silent prompt=none in flight (handled in the engine)
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.queryByLabelText('Email')).toBeNull();          // no legacy form
    expect(screen.getByText(/Signing you in/i)).toBeInTheDocument();
    // The engine owns the silent probe; the screen must NOT auto-fire an interactive redirect
    // (auto-parking on Keycloak's form is what looped restart-cookie across tabs).
    await waitFor(() => expect(loginWithSSO).not.toHaveBeenCalled());
  });

  it('shows an explicit Sign-in button once the silent probe found no session, and only redirects on click', async () => {
    ssoOnline = true; needsInteractiveLogin = true;
    const user = userEvent.setup();
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.queryByLabelText('Email')).toBeNull();          // still no legacy form in CR mode
    const btn = screen.getByRole('button', { name: /Sign in with CrimsonRaven/i });
    expect(loginWithSSO).not.toHaveBeenCalled();                  // not until the user clicks
    await user.click(btn);
    expect(loginWithSSO).toHaveBeenCalledTimes(1);
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
});
