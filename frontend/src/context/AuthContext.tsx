import { createContext, useState, useContext, useEffect, ReactNode } from 'react';
import { getUserManager, loadRuntimeConfig } from '../lib/oidc';

/** Set when an SSO sign-in is held by the backend (unverified email matching an existing
 *  account). Its presence tells LoginPage to stop auto-redirecting to the IdP and offer a way
 *  out instead; the value is the human message to show. Cleared on any successful session/logout. */
export const SSO_BLOCKED_KEY = 'sso_blocked';

/** Holds the still-valid IdP token + subject of a held sign-in, so the "resend verification email"
 *  action can call CrimsonRaven's self-service ResendEmailCode with the user's OWN token (no admin role). */
export const SSO_PENDING_KEY = 'sso_pending';

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
  /** Resend the verification email for a held (unverified) sign-in — CR self-service, user's own token. */
  resendVerification: () => Promise<void>;
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
    localStorage.removeItem(SSO_BLOCKED_KEY); // a real session clears any prior "verify email" hold
    localStorage.removeItem(SSO_PENDING_KEY);
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
    if (!res.ok) {
      // Email-unverified HOLD: the IdP signed us in, but the backend won't map the session until
      // the email is verified. We must NOT fall back into the SSO redirect — the IdP session is
      // live, so it would silently re-auth and 403 forever (the dead loop). Flag it so LoginPage
      // shows a way out (use email+password, or log out) instead, and drop the unusable token.
      if (res.status === 403) {
        const body = await res.json().catch(() => ({} as { error?: string; message?: string }));
        if (body?.error === 'email_unverified') {
          localStorage.removeItem('token');
          setToken(null);
          // Keep the (still-valid) token + subject so the held screen can resend the verification
          // email via CR self-service. sub = the IdP subject (Zitadel userId).
          localStorage.setItem(SSO_PENDING_KEY,
            JSON.stringify({ token: accessToken, sub: oidcUser.profile.sub }));
          localStorage.setItem(SSO_BLOCKED_KEY,
            body.message || 'Please verify your email, then sign in again.');
          throw new Error('email_unverified');
        }
      }
      throw new Error(await errorMessage(res, 'Could not establish your session.'));
    }
    const data = await res.json();
    // CrimsonRaven gives us a real name (profile scope) — prefer the first name for the header.
    const name = oidcUser.profile.given_name || oidcUser.profile.name || undefined;
    setSession(accessToken, { id: data.userId, email: data.email, name });
  };

  // Resend the email-verification mail for a held sign-in, using the user's OWN (still-valid) token
  // against CrimsonRaven's self-service ResendEmailCode — permission "authenticated", no admin role.
  const resendVerification = async () => {
    const raw = localStorage.getItem(SSO_PENDING_KEY);
    if (!raw) throw new Error('Your session expired — sign in again to resend.');
    const { token: heldToken } = JSON.parse(raw) as { token: string };
    // Our backend proxies to CrimsonRaven with a role-less PAT — the user's own JWT can't call CR's
    // API directly (the instance audience bug). The held token only authenticates us to our backend,
    // which reads the sub from it and triggers the resend.
    const res = await fetch('/api/auth/resend-verification', {
      method: 'POST',
      headers: { Authorization: `Bearer ${heldToken}` },
    });
    if (!res.ok) throw new Error(await errorMessage(res, 'Could not send the verification email.'));
  };

  const logout = async () => {
    const mgr = await getUserManager();
    const oidcUser = mgr ? await mgr.getUser().catch(() => null) : null;
    // Clear the persisted session first so any return from the IdP — or a local logout —
    // lands logged-out (AuthProvider seeds user/token from these on mount).
    localStorage.removeItem('token');
    localStorage.removeItem('user');
    localStorage.removeItem(SSO_BLOCKED_KEY);
    localStorage.removeItem(SSO_PENDING_KEY);
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
      value={{ user, token, ssoOnline, ssoConfigured, authReady, login, register, loginWithSSO, completeSsoCallback, resendVerification, logout }}>
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
