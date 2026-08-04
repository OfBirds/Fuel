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

// LoginPage's useAuth comes from '../context/AuthContext', which re-exports auth-core's — so mocking
// auth-core's useAuth here covers it. The SSO screen is auth-core's own <SsoCard> (its button/spinner
// behaviour is auth-core's concern, driven by the engine's needsInteractiveLogin); here we stub it and
// just assert LoginPage picks SSO mode (renders SsoCard, not the legacy form).
vi.mock('@bearsoft/auth-core/react', () => ({
  useAuth: () => ({
    user: null, token: null, login, register, loginWithSSO,
    ssoOnline, ssoConfigured, authReady, needsInteractiveLogin, logout: vi.fn(),
  }),
  SsoCard: ({ brand }: { brand: string }) => <div data-testid="sso-card">{brand}</div>,
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

  it('renders the shared SsoCard (never the legacy form, no auto-redirect) when CrimsonRaven is online', async () => {
    ssoOnline = true;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.queryByLabelText('Email')).toBeNull();            // no legacy form in CR mode
    expect(screen.getByTestId('sso-card')).toHaveTextContent('Indigo Swallow'); // shared card + Fuel wordmark
    // The engine owns the silent probe; LoginPage must NOT itself fire an interactive redirect.
    await waitFor(() => expect(loginWithSSO).not.toHaveBeenCalled());
  });

  it('falls back to the legacy form + maintenance notice when Raven is configured but down', () => {
    ssoConfigured = true; ssoOnline = false;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.getByText(/Crimson Raven is offline/i)).toBeInTheDocument();
    expect(loginWithSSO).not.toHaveBeenCalled();
  });

  it('shows the plain legacy form (no notice) when SSO is not configured — local dev', () => {
    ssoConfigured = false; ssoOnline = false;
    render(<LoginPage onLoginSuccess={() => {}} />);
    expect(screen.getByLabelText('Email')).toBeInTheDocument();
    expect(screen.queryByText(/Crimson Raven is offline/i)).toBeNull();
  });
});
