import { useState, useEffect } from 'react';
import { useAuth, SSO_BLOCKED_KEY } from '../context/AuthContext';
import { useOidcLogo } from '../hooks/useOidcLogo';
import '../styles/login.css';

function EyeIcon({ visible }: { visible: boolean }) {
  return visible ? (
    <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94"/>
      <path d="M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19"/>
      <line x1="1" y1="1" x2="23" y2="23"/>
    </svg>
  ) : (
    <svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/>
      <circle cx="12" cy="12" r="3"/>
    </svg>
  );
}

interface LoginPageProps {
  onLoginSuccess: () => void;
}

type View = 'login' | 'register' | 'forgot';

// Kept in sync with the backend policy in AuthService.IsPasswordValid.
const PASSWORD_RULES: { label: string; test: (p: string) => boolean }[] = [
  { label: 'At least 8 characters', test: (p) => p.length >= 8 },
  { label: 'One letter', test: (p) => /[a-zA-Z]/.test(p) },
  { label: 'One number', test: (p) => /[0-9]/.test(p) },
  { label: 'One special character', test: (p) => /[^a-zA-Z0-9]/.test(p) },
];

function PasswordRequirements({ value }: { value: string }) {
  return (
    <ul className="pw-reqs" aria-label="Password requirements">
      {PASSWORD_RULES.map((rule) => {
        const met = rule.test(value);
        return (
          <li key={rule.label} className={met ? 'pw-req met' : 'pw-req'}>
            <span aria-hidden="true">{met ? '✓' : '○'}</span> {rule.label}
          </li>
        );
      })}
    </ul>
  );
}

const SUBTITLES: Record<View, string> = {
  login: 'Welcome back',
  register: 'Create your account',
  forgot: 'Reset your password',
};

function viewFromHash(): View {
  const h = window.location.hash;
  if (h === '#register') return 'register';
  if (h === '#forgot') return 'forgot';
  return 'login';
}

function LoginPage({ onLoginSuccess }: LoginPageProps) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [showNewPassword, setShowNewPassword] = useState(false);
  const [view, setView] = useState<View>(viewFromHash);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [loading, setLoading] = useState(false);
  const { login, register, loginWithSSO, logout, ssoOnline, ssoConfigured, authReady } = useAuth();
  // Set by the SSO callback when the backend held this sign-in (unverified email matching an
  // existing account). While set, we must NOT auto-redirect to CrimsonRaven — its session is
  // live, so it would re-auth and 403 in an endless loop. Show the way-out form instead.
  const [ssoBlocked, setSsoBlocked] = useState(() => localStorage.getItem(SSO_BLOCKED_KEY));
  const [showLegacy, setShowLegacy] = useState(false); // held screen: reveal the password form on demand

  // CrimsonRaven is the front door: when it's online, send the user straight there — no
  // choice. The legacy email/password form below is only reached when Raven is down or
  // unconfigured (break-glass / local dev), or when a sign-in is held (ssoBlocked).
  useEffect(() => {
    if (!authReady || !ssoOnline || ssoBlocked) return;
    loginWithSSO().catch((err) =>
      setError(err instanceof Error ? err.message : 'Could not reach CrimsonRaven.'));
  }, [authReady, ssoOnline, ssoBlocked, loginWithSSO]);

  // Held sign-in actions: retry the IdP (after verifying the email) or fully log out of it.
  const retrySso = () => {
    localStorage.removeItem(SSO_BLOCKED_KEY);
    setSsoBlocked(null);
    setError('');
    loginWithSSO().catch((e) =>
      setError(e instanceof Error ? e.message : 'Could not reach CrimsonRaven.'));
  };
  const exitSso = () => {
    localStorage.removeItem(SSO_BLOCKED_KEY);
    setSsoBlocked(null);
    logout();
  };

  // CrimsonRaven's own logo (themed), pulled live from the IdP — shown on the redirect screen.
  const logoSrc = useOidcLogo();

  const resetForm = () => {
    setError('');
    setSuccess('');
    setPassword('');
    setNewPassword('');
  };

  const switchView = (v: View) => {
    resetForm();
    setView(v);
    window.location.hash = v === 'login' ? '' : v;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setSuccess('');
    setLoading(true);

    try {
      if (view === 'forgot') {
        const res = await fetch('/api/auth/reset-password', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ email, newPassword }),
        });
        if (!res.ok) {
          const text = await res.text();
          let message = 'Reset failed';
          try { message = JSON.parse(text).error ?? message; } catch {}
          throw new Error(message);
        }
        setSuccess('Password reset! You can now sign in.');
        setView('login');
        setPassword('');
        setNewPassword('');
      } else if (view === 'register') {
        await register(email, password);
        onLoginSuccess();
      } else {
        await login(email, password);
        onLoginSuccess();
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'An error occurred');
    } finally {
      setLoading(false);
    }
  };

  // Until we know whether CrimsonRaven is up — or while bouncing to it — show a spinner,
  // never the legacy form (no flash, and SSO is the only path when Raven is online). When a
  // sign-in is held (ssoBlocked) we skip the redirect and fall through to the way-out form.
  if (!authReady || (ssoOnline && !ssoBlocked)) {
    return (
      <div className="login-page">
        <div className="login-container">
          <h1>Indigo Swallow</h1>
          {error ? (
            <>
              <p className="error-message">{error}</p>
              <button type="button" className="submit-button"
                onClick={() => { setError(''); loginWithSSO().catch((e) => setError(e instanceof Error ? e.message : 'Could not reach CrimsonRaven.')); }}>
                Try again
              </button>
            </>
          ) : (
            <>
              {ssoOnline && logoSrc && <img src={logoSrc} alt="CrimsonRaven" className="redirect-logo" />}
              <p className="login-subtitle">{ssoOnline ? 'Redirecting to CrimsonRaven…' : 'Loading…'}</p>
            </>
          )}
        </div>
      </div>
    );
  }

  // Held sign-in (unverified email): a focused "verify your email" screen — NOT the full legacy
  // login, which confuses CrimsonRaven users (a Log-out button next to a password form). The
  // email/password path is tucked behind a disclosure for the migration users who actually have one.
  if (ssoBlocked && !showLegacy) {
    return (
      <div className="login-page">
        <div className="login-container">
          <h1>Indigo Swallow</h1>
          <p className="login-subtitle">Verify your email to finish signing in</p>
          <div className="maintenance-note">{ssoBlocked}</div>
          {error && <div className="error-message">{error}</div>}
          <button type="button" className="submit-button" onClick={retrySso}>
            I've verified — try again
          </button>
          <div className="login-toggle" style={{ marginTop: '0.75rem' }}>
            <button type="button" className="toggle-button" onClick={exitSso}>
              Log out / use a different account
            </button>
            <button type="button" className="toggle-button" onClick={() => { setError(''); setShowLegacy(true); }}>
              Sign in with a password instead
            </button>
          </div>
        </div>
      </div>
    );
  }

  // Legacy email/password — reached when CrimsonRaven is down/unconfigured, or when a held user
  // explicitly chose the password path above.
  return (
    <div className="login-page">
      <div className="login-container">
        <h1>Indigo Swallow</h1>
        {ssoBlocked ? (
          <div className="maintenance-note">
            Signing in with your email &amp; password reaches your data now. To use CrimsonRaven,
            verify your email first, then{' '}
            <button type="button" className="toggle-button" onClick={retrySso}>try again</button>.
          </div>
        ) : ssoConfigured ? (
          <div className="maintenance-note">
            <strong>CrimsonRaven is offline.</strong> Sign in or register with the same email to
            reach your data until it's back.
          </div>
        ) : null}
        <p className="login-subtitle">{SUBTITLES[view]}</p>

        <form onSubmit={handleSubmit} className="login-form">
          <div className="form-group">
            <label htmlFor="email">Email</label>
            <input
              type="email"
              id="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              placeholder="you@example.com"
            />
          </div>

          {view !== 'forgot' && (
            <div className="form-group">
              <label htmlFor="password">Password</label>
              <div className="password-wrapper">
                <input
                  type={showPassword ? 'text' : 'password'}
                  id="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                  placeholder="••••••••"
                />
                <button type="button" className="eye-button" onClick={() => setShowPassword(v => !v)} aria-label="Toggle password visibility">
                  <EyeIcon visible={showPassword} />
                </button>
              </div>
              {view === 'register' && <PasswordRequirements value={password} />}
            </div>
          )}

          {view === 'forgot' && (
            <div className="form-group">
              <label htmlFor="newPassword">New Password</label>
              <div className="password-wrapper">
                <input
                  type={showNewPassword ? 'text' : 'password'}
                  id="newPassword"
                  value={newPassword}
                  onChange={(e) => setNewPassword(e.target.value)}
                  required
                  placeholder="Your new password"
                  minLength={8}
                />
                <button type="button" className="eye-button" onClick={() => setShowNewPassword(v => !v)} aria-label="Toggle password visibility">
                  <EyeIcon visible={showNewPassword} />
                </button>
              </div>
              <PasswordRequirements value={newPassword} />
            </div>
          )}

          {error && <div className="error-message">{error}</div>}
          {success && <div className="success-message">{success}</div>}

          <button type="submit" disabled={loading} className="submit-button">
            {loading
              ? 'Loading...'
              : view === 'forgot'
              ? 'Reset Password'
              : view === 'register'
              ? 'Create Account'
              : 'Sign In'}
          </button>
        </form>

        {view === 'login' && (
          <div className="login-toggle">
            <button type="button" onClick={() => switchView('forgot')} className="toggle-button forgot-link">
              Forgot password?
            </button>
            <p>
              Don't have an account?
              <button type="button" onClick={() => switchView('register')} className="toggle-button">
                Create Account
              </button>
            </p>
          </div>
        )}

        {view === 'register' && (
          <div className="login-toggle">
            <p>
              Already have an account?
              <button type="button" onClick={() => switchView('login')} className="toggle-button">
                Sign In
              </button>
            </p>
          </div>
        )}

        {view === 'forgot' && (
          <div className="login-toggle">
            <p>
              Remember it after all?
              <button type="button" onClick={() => switchView('login')} className="toggle-button">
                Back to Sign In
              </button>
            </p>
          </div>
        )}

        <div className="disclaimer">
          <p>
            <strong>Self-hosted &amp; private.</strong> Indigo Swallow runs on your own server — your food
            log and weigh-ins never leave it.
          </p>
        </div>
      </div>
    </div>
  );
}

export default LoginPage;
