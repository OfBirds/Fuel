# Auth — CrimsonRaven SSO (dual-auth, data-preserving)

> Status: **live** (dual-auth, CrimsonRaven-first). The app accepts **both** CrimsonRaven
> (OIDC) tokens **and** its own legacy HMAC JWT. The login screen is **CrimsonRaven-first**:
> when the IdP is reachable the user is sent straight to it; the email/password form is only
> shown when CrimsonRaven is offline/unconfigured (break-glass), with a maintenance notice.
> Decommissioning the local path is a deliberate later step. Running on staging over HTTPS
> (`https://raven-staging.bearsoft.duckdns.org`).
>
> (The app is branded **Indigo Swallow**; the repo/code namespace stays `fuel`.)

## Why

Move authentication to **CrimsonRaven** (self-hosted Zitadel IdP) for SSO across homelab
apps, MFA/TOTP, Google login, and refresh/sessions — without dropping the working local
login or, critically, **losing any existing user's data**.

## Identity model — link-by-email (the load-bearing bit)

Fuel's `User.Id` (GUID) owns all rows (`FoodEntry`, `WeightEntry`, profile fields).
That id must never change, so the IdP `sub` is **mapped onto** the existing user rather
than replacing it:

- `User.ExternalSubject` (nullable, unique) stores the CrimsonRaven `sub` once linked.
- `OidcUserProvisioner` (`IClaimsTransformation`, `Services/OidcUserProvisioner.cs`) runs
  per request for a CrimsonRaven principal and:
  1. finds the user by `ExternalSubject`; else
  2. if the token's email is **verified**, links by case-insensitive email to the
     existing row (this is what preserves entries/weights/profile); else
  3. creates a new password-less user.
- It then rewrites the principal's `sub` to the Fuel `User.Id`, so
  `ResourceOwnershipFilter` and every `api/user/{userId}/...` route are **unchanged**.
- Idempotent: a GUID `sub` (Fuel-issued token, or already-mapped) is a no-op.

**Users are not copied into CrimsonRaven and passwords are not migrated** (Fuel's PBKDF2
hashes aren't a Zitadel-verifiable format). Existing users **self-register** in
CrimsonRaven with the same email (or use Google); first login links by verified email.
A user who hasn't verified their email is treated as new (empty) until they do.

## Backend

- **Dual bearer schemes** (`Program.cs`): `"Fuel"` (HMAC, unchanged) and `"CrimsonRaven"`
  (`AddJwtBearer` against `OIDC_AUTHORITY`/JWKS, `aud = OIDC_AUDIENCE`). A `"smart"` policy
  scheme is the default and forwards by peeking the bearer's `iss`. The default-deny
  `FallbackPolicy` is unchanged; both schemes satisfy it.
- `RequireHttpsMetadata` is derived from the authority scheme (http homelab vs https prod).
- OIDC is **opt-in**: blank `OIDC_AUTHORITY` → only the Fuel scheme runs.
- **`GET /api/auth/me`** (authenticated) returns `{ userId, email }` — the SPA calls it
  after the OIDC callback to learn its Fuel `User.Id`.
- **`GET /api/config`** (anonymous) lets the single Docker image be configured per-stack at
  runtime (no rebuild). It returns `{ oidcEnabled, oidcAuthority, oidcClientId, oidcOnline,
  oidcLogoUrl, oidcLogoUrlDark }`:
  - `oidcOnline` — a short-cached liveness probe of the IdP's discovery endpoint; drives the
    CrimsonRaven-first vs offline-fallback decision on the login screen.
  - `oidcLogoUrl` / `oidcLogoUrlDark` — CrimsonRaven's own (themed) logo, scraped from its
    login page and cached, so the app shows the IdP's mark on the redirect/callback screens
    (single source of truth = CrimsonRaven).

## Frontend

- `oidc-client-ts` `UserManager`, configured at runtime from `/api/config`
  (`src/lib/oidc.ts`). Authorization Code + PKCE, `scope "openid profile email
  offline_access"`, silent renew via refresh token.
- `AuthContext` exposes `loginWithSSO()` (redirect), `completeSsoCallback()`, and the state
  `ssoOnline` / `ssoConfigured` / `authReady` (from `/api/config`). The `/auth/callback`
  route (`AuthCallbackPage`) finishes the code exchange, calls `/api/auth/me`, and stores
  `token` + `user {id,email}` the **same** way as the local path — so `apiFetch`'s Bearer
  attach and 401→clear are unchanged. **Logout** calls `signoutRedirect()` when an OIDC
  session exists (ends the CrimsonRaven session so a different account can sign in), else a
  local clear.
- `LoginPage` is **CrimsonRaven-first**, not a choice: when `authReady && ssoOnline` it
  auto-redirects via `loginWithSSO()` (showing a spinner). The legacy email/password form is
  reached **only** when CrimsonRaven is down/unconfigured, and then shows a maintenance note
  ("CrimsonRaven is offline — sign in/register with the same email to reach your data").
- `useOidcLogo()` pulls CrimsonRaven's themed logo from `/api/config` for the
  redirect/callback screens, so the IdP's brand is shown while bouncing.

## Config (flat env, per stack)

| Key | Meaning |
|---|---|
| `OIDC_AUTHORITY` | IdP issuer; must equal the token `iss`. Blank → SSO off. |
| `OIDC_CLIENT_ID` | Fuel public/PKCE client id in CrimsonRaven. |
| `OIDC_AUDIENCE` | Expected `aud` in the access token. |

- **Local + staging** → `https://raven-staging.bearsoft.duckdns.org` (homelab Zitadel, now
  HTTPS — see `CrimsonRaven/docs/https-migration.md`; client `377946274235744259`). HTTPS is
  required so the SPA's `crypto.subtle`/PKCE and Login V2's session cookie work (secure
  context). Staging is served at `https://fuel-staging.bearsoft.duckdns.org` for the same
  reason.
- **Prod** → `https://raven.bearsoft.duckdns.org` (separate instance; its own Fuel app +
  user store — register that app + fill prod `OIDC_*` when standing prod SSO up).

## Verify

- `dotnet test backend/Fuel.slnx -c Release` — `OidcUserProvisionerTests` (link/verify/
  idempotency), `ResourceOwnershipFilter` still 403s cross-user, local JWT path intact.
- `npm test --prefix frontend -- --run` — SSO button visibility + redirect trigger.
- End-to-end (needs a real CrimsonRaven account with a verified, real email): SSO button →
  CrimsonRaven login → `/auth/callback` → day view shows that user's existing data;
  the legacy email/password login still works.

## Resolved during build

- **Access-token claims.** Zitadel's access token does **not** carry `email`/`email_verified`,
  so `OidcUserProvisioner` falls back to a **userinfo lookup** (`{authority}/oidc/v1/userinfo`
  with the request's bearer, via `IHttpContextAccessor`) to get the verified email for
  link-by-email. No Zitadel Action needed.

## Open / later

- **Phase 4 cutover** (separate): remove `JwtTokenService`, password logic, `/api/auth/*`,
  the `"Fuel"` scheme, and drop `PasswordHash`.
- **Prod SSO**: register a Fuel OIDC app on the prod CrimsonRaven and fill prod `OIDC_*`.
