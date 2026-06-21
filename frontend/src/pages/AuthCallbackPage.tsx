import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth, SSO_BLOCKED_KEY } from '../context/AuthContext';
import { useOidcLogo } from '../hooks/useOidcLogo';
import '../styles/login.css';

// Lands here after the CrimsonRaven redirect. Completes the PKCE code exchange, resolves
// the Fuel session, then returns to the app. On failure, shows a message + a way back.
function AuthCallbackPage() {
  const { completeSsoCallback } = useAuth();
  const navigate = useNavigate();
  const logoSrc = useOidcLogo();
  const [error, setError] = useState('');
  const ran = useRef(false); // guard React 18 StrictMode double-invoke (code is single-use)

  useEffect(() => {
    if (ran.current) return;
    ran.current = true;
    (async () => {
      try {
        await completeSsoCallback();
        navigate('/', { replace: true });
      } catch (e) {
        // Email-unverified hold → go back to the login screen, which shows the way out
        // (use email+password, or log out). Do NOT show an error here: that would strand
        // the user on the callback page with no escape from the SSO redirect.
        if (localStorage.getItem(SSO_BLOCKED_KEY)) {
          navigate('/', { replace: true });
          return;
        }
        setError(e instanceof Error ? e.message : 'Sign-in failed.');
      }
    })();
  }, [completeSsoCallback, navigate]);

  return (
    <div className="login-page">
      <div className="login-container">
        <h1>Indigo Swallow</h1>
        {error ? (
          <>
            <p className="error-message">{error}</p>
            <button className="submit-button" onClick={() => navigate('/', { replace: true })}>
              Back to sign in
            </button>
          </>
        ) : (
          <>
            {logoSrc && <img src={logoSrc} alt="CrimsonRaven" className="redirect-logo" />}
            <p className="login-subtitle">Signing you in…</p>
          </>
        )}
      </div>
    </div>
  );
}

export default AuthCallbackPage;
