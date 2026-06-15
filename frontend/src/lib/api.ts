import { getToken, clearSession } from './storage';

/**
 * fetch() for the app's own API. Attaches the bearer token from storage, and on a 401
 * (expired / forged / cleared token) tears down the session and bounces to login by doing
 * a hard navigation to "/" — AuthProvider re-initialises with no user, which renders
 * LoginPage. Auth endpoints (login/register/reset) don't need a token and call fetch directly.
 */
export async function apiFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const headers = new Headers(init.headers);
  const token = getToken();
  if (token) headers.set('Authorization', `Bearer ${token}`);

  const res = await fetch(input, { ...init, headers });

  if (res.status === 401) {
    clearSession();
    window.location.assign('/');
  }
  return res;
}
