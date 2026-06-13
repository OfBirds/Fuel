import { useState } from 'react';
import { useAuth } from '../context/AuthContext';
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

function LoginPage({ onLoginSuccess }: LoginPageProps) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [showNewPassword, setShowNewPassword] = useState(false);
  const [view, setView] = useState<View>('login');
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const [loading, setLoading] = useState(false);
  const { login, register } = useAuth();

  const resetForm = () => {
    setError('');
    setSuccess('');
    setPassword('');
    setNewPassword('');
  };

  const switchView = (v: View) => {
    resetForm();
    setView(v);
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

  return (
    <div className="login-page">
      <div className="login-container">
        <h1>Fuel</h1>
        <p className="login-subtitle">Sign in to continue</p>

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
                  placeholder="Min. 8 characters"
                  minLength={8}
                />
                <button type="button" className="eye-button" onClick={() => setShowNewPassword(v => !v)} aria-label="Toggle password visibility">
                  <EyeIcon visible={showNewPassword} />
                </button>
              </div>
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
            <strong>Welcome.</strong> Create an account or sign in. Your data is private and stored securely.
          </p>
        </div>
      </div>
    </div>
  );
}

export default LoginPage;
