import { createContext, useState, useContext, useEffect, ReactNode } from 'react';
import { getUserManager, loadRuntimeConfig } from '../lib/oidc';

interface User {
  id: string;
  email: string;
  /** Display name from CrimsonRaven (OIDC `given_name`/`name`). Absent on the local
   *  email/password path — the header falls back to email when this is empty. */
  name?: string;
}

interface AuthContextType {
  user: User | null;
  token: string | null;
  /** CrimsonRaven is configured AND reachable now → login goes straight to it (no choice). */
  ssoOnline: boolean;
  /** CrimsonRaven is configured for this stack (regardless of reachability) — drives the
   *  "IdP is down" maintenance notice on the legacy fallback form. */
  ssoConfigured: boolean;
  /** Runtime config (the sso* flags) has been resolved — gates the login screen so it
   *  doesn't flash the legacy form before we know whether to redirect to CrimsonRaven. */
  authReady: boolean;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string) => Promise<void>;
  /** Kick off the CrimsonRaven PKCE redirect. */
  loginWithSSO: () => Promise<void>;
  /** Finish the OIDC redirect (called from /auth/callback). */
  completeSsoCallback: () => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

/** Pull the API's `{ error }` message out of a failed response, falling back to a default. */
async function errorMessage(res: Response, fallback: string): Promise<string> {
  try {
    const data = await res.json();
    return typeof data?.error === 'string' && data.error ? data.error : fallback;
  } catch {
    return fallback;
  }
}

function persist(token: string, user: User) {
  localStorage.setItem('token', token);
  localStorage.setItem('user', JSON.stringify(user));
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(() => {
    const stored = localStorage.getItem('user');
    return stored ? JSON.parse(stored) : null;
  });
  const [token, setToken] = useState<string | null>(() => localStorage.getItem('token'));
  const [ssoOnline, setSsoOnline] = useState(false);
  const [ssoConfigured, setSsoConfigured] = useState(false);
  const [authReady, setAuthReady] = useState(false);

  // CrimsonRaven is the front door when it's reachable (ssoOnline → login redirects straight
  // to it). If it's configured but down (ssoConfigured && !ssoOnline) the legacy email/password
  // form is shown as the break-glass fallback; if it's not configured at all (local dev) the
  // legacy form is just the normal login. Also keep the stored bearer fresh on OIDC renewal.
  useEffect(() => {
    let alive = true;
    (async () => {
      const cfg = await loadRuntimeConfig();
      if (!alive) return;
      setSsoConfigured(!!cfg.oidcEnabled);
      setSsoOnline(!!(cfg.oidcEnabled && cfg.oidcOnline));
      setAuthReady(true);
      if (!cfg.oidcEnabled) return;
      const mgr = await getUserManager();
      mgr?.events.addUserLoaded((u) => {
        setToken(u.access_token);
        localStorage.setItem('token', u.access_token);
      });
    })();
    return () => { alive = false; };
  }, []);

  const setSession = (newToken: string, newUser: User) => {
    setUser(newUser);
    setToken(newToken);
    persist(newToken, newUser);
  };

  const login = async (email: string, password: string) => {
    const res = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    if (!res.ok) throw new Error(await errorMessage(res, 'Invalid email or password'));
    const data = await res.json();
    setSession(data.token, { id: data.userId, email: data.email });
  };

  const register = async (email: string, password: string) => {
    const res = await fetch('/api/auth/register', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ email, password }),
    });
    if (!res.ok) throw new Error(await errorMessage(res, 'Registration failed'));
    const data = await res.json();
    setSession(data.token, { id: data.userId, email: data.email });
  };

  const loginWithSSO = async () => {
    const mgr = await getUserManager();
    if (!mgr) throw new Error('SSO is not configured.');
    await mgr.signinRedirect();
  };

  // Completes the code exchange, then asks the backend who we are: the OIDC token's `sub`
  // is the IdP subject, but the backend maps it to (and returns) our Fuel User.Id, which
  // is what every api/user/{id} route needs.
  const completeSsoCallback = async () => {
    const mgr = await getUserManager();
    if (!mgr) throw new Error('SSO is not configured.');
    const oidcUser = await mgr.signinRedirectCallback();
    const accessToken = oidcUser.access_token;
    localStorage.setItem('token', accessToken); // so apiFetch attaches it on /me
    setToken(accessToken);
    const res = await fetch('/api/auth/me', { headers: { Authorization: `Bearer ${accessToken}` } });
    if (!res.ok) throw new Error(await errorMessage(res, 'Could not establish your session.'));
    const data = await res.json();
    // CrimsonRaven gives us a real name (profile scope) — prefer the first name for the header.
    const name = oidcUser.profile.given_name || oidcUser.profile.name || undefined;
    setSession(accessToken, { id: data.userId, email: data.email, name });
  };

  const logout = async () => {
    const mgr = await getUserManager();
    const oidcUser = mgr ? await mgr.getUser().catch(() => null) : null;
    // Clear the persisted session first so any return from the IdP — or a local logout —
    // lands logged-out (AuthProvider seeds user/token from these on mount).
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    if (mgr && oidcUser) {
      // End the CrimsonRaven session and leave the page. Crucially, do NOT setUser(null)
      // first: that remounts LoginPage, whose Raven-first effect fires signinRedirect and
      // races (and usually beats) this signout — so the IdP cookie survives and you're
      // silently re-authed straight back in. Navigating away here avoids the race entirely.
      try {
        await mgr.signoutRedirect();
        return; // page is now navigating to the IdP end-session endpoint
      } catch {
        await mgr.removeUser().catch(() => {});
      }
    }
    // Local (non-SSO) logout, or signout failed to start: clear in-app state.
    setUser(null);
    setToken(null);
  };

  return (
    <AuthContext.Provider
      value={{ user, token, ssoOnline, ssoConfigured, authReady, login, register, loginWithSSO, completeSsoCallback, logout }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used within AuthProvider');
  }
  return context;
}
