// OIDC (PKCE) client for CrimsonRaven SSO. The authority + client id are NOT baked at
// build time — the one Docker image is promoted across stacks that point at different
// CrimsonRaven instances (homelab http vs prod https). So we fetch them at runtime from
// the backend's /api/config and lazily build a single UserManager. When OIDC isn't
// configured for the stack, getUserManager() resolves to null and the SSO UI stays hidden.
import { UserManager, WebStorageStateStore } from 'oidc-client-ts';

export interface RuntimeConfig {
  oidcEnabled: boolean;   // CrimsonRaven is configured for this stack
  oidcOnline?: boolean;   // ...and reachable right now (drives Raven-first vs local fallback)
  oidcAuthority?: string;
  oidcClientId?: string;
  oidcLogoUrl?: string;     // CrimsonRaven's current logo (light), scraped from its login page
  oidcLogoUrlDark?: string; // ...and the dark variant
}

let configPromise: Promise<RuntimeConfig> | null = null;

export function loadRuntimeConfig(): Promise<RuntimeConfig> {
  configPromise ??= fetch('/api/config')
    .then((r) => (r.ok ? r.json() : { oidcEnabled: false }))
    .catch(() => ({ oidcEnabled: false }) as RuntimeConfig);
  return configPromise;
}

let manager: UserManager | null = null;

export async function getUserManager(): Promise<UserManager | null> {
  const cfg = await loadRuntimeConfig();
  if (!cfg.oidcEnabled || !cfg.oidcAuthority || !cfg.oidcClientId) return null;
  manager ??= new UserManager({
    authority: cfg.oidcAuthority,
    client_id: cfg.oidcClientId,
    redirect_uri: `${window.location.origin}/auth/callback`,
    post_logout_redirect_uri: window.location.origin,
    response_type: 'code',
    // The reserved `…:zitadel:aud` scope puts the Zitadel API in the access token's audience, so the
    // user's OWN token can call self-service endpoints like POST /v2/users/{sub}/email/resend
    // (ResendEmailCode is permission "authenticated" — no manager role needed). Used by the held
    // "resend verification email" action.
    scope: 'openid profile email offline_access urn:zitadel:iam:org:project:id:zitadel:aud',
    // Keep the session out of the URL and renew silently using the refresh token.
    userStore: new WebStorageStateStore({ store: window.localStorage }),
    automaticSilentRenew: true,
  });
  return manager;
}
