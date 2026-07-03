# Auth — CrimsonRaven SSO (dual-auth, data-preserving)

> Status: **live** (dual-auth, CrimsonRaven-first). The app accepts **both** CrimsonRaven
> (OIDC) tokens **and** its own legacy HMAC JWT. The login screen is **CrimsonRaven-first**:
> when the IdP is reachable the user is sent straight to it; the email/password form is only
> shown when CrimsonRaven is offline/unconfigured (break-glass), with a maintenance notice.
> Decommissioning the local path is a deliberate later step.
>
> (The app is branded **Indigo Swallow**; the repo/code namespace stays `fuel`.)

> **Self-hosting note:** *CrimsonRaven* is just the maintainer's own OIDC identity provider.
> SSO is provider-agnostic — point `OIDC_AUTHORITY`/`OIDC_CLIENT_ID`/`OIDC_AUDIENCE` at **any**
> OIDC provider (Keycloak, Authentik, Zitadel, …), or leave them blank to use only the
> built-in email/password login. Everything below describes the maintainer's CrimsonRaven
> integration and the data-preserving link-by-email design, which applies to any provider.

**CrimsonRaven is a Keycloak IdP.** (It ran Zitadel earlier; the migration to Keycloak
removed the Zitadel-era app-side workarounds — an unverified-email hold middleware, a
resend-verification endpoint + mailer PAT, and an IdP logo-scrape. Keycloak now hosts the
themed login plus native email-verification, resend and forgot-password, so
`OidcUserProvisioner` links by email whenever Keycloak reports the email verified.)

## Why

Move authentication to **CrimsonRaven** (a self-hosted Keycloak IdP) for SSO across homelab
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
hashes aren't a Keycloak-verifiable format). Existing users **self-register** in
CrimsonRaven with the same email (or use Google); first login links by verified email.
A user who hasn't verified their email is treated as new (empty) until they do.

## Backend

- **Dual bearer schemes** (`Program.cs`): `"Fuel"` (HMAC, unchanged) and `"CrimsonRaven"`
  (`AddJwtBearer` against `OIDC_AUTHORITY`/JWKS, `aud = OIDC_AUDIENCE`). A `"smart"` policy
  scheme is the default and forwards by peeking the bearer's `iss`. The default-deny
  `FallbackPolicy` is unchanged; both schemes satisfy it.
- `RequireHttpsMetadata` is derived from the authority scheme (a plain-http LAN dev IdP vs
  an https IdP).
- OIDC is **opt-in**: blank `OIDC_AUTHORITY` → only the Fuel scheme runs. When it *is* set,
  `OIDC_AUDIENCE` is **required** — the app refuses to start without it, so audience
  validation can't be silently skipped (a token minted for a different client on the same
  IdP would otherwise validate).
- **`GET /api/auth/me`** (authenticated) returns `{ userId, email }` — the SPA calls it
  after the OIDC callback to learn its Fuel `User.Id`.
- **`GET /api/config`** (anonymous) lets the single Docker image be configured per-stack at
  runtime (no rebuild). It returns `{ oidcEnabled, oidcOnline, oidcAuthority, oidcClientId,
  authMode }`:
  - `oidcOnline` — a short-cached liveness probe of the IdP's discovery endpoint; drives the
    CrimsonRaven-first vs offline-fallback decision on the login screen. A caller-aborted
    probe is not cached (so a closed tab can't leave a false "offline"), and negative results
    use a shorter TTL than positive ones.
  - `authMode` — `crimsonraven` (default) or `legacy`. A manual env break-glass
    (`AUTH_MODE=legacy`) forces the local email/password form during CR maintenance; never
    both at once.
  - Branding (logo, theme) is owned by Keycloak's own login pages — the app no longer scrapes
    a logo from the IdP.

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

## Config (flat env, per stack)

| Key | Meaning |
|---|---|
| `OIDC_AUTHORITY` | IdP issuer; must equal the token `iss`. Blank → SSO off. |
| `OIDC_CLIENT_ID` | Fuel public/PKCE client id in CrimsonRaven. |
| `OIDC_AUDIENCE` | Expected `aud` in the access token. Required when `OIDC_AUTHORITY` is set. |
| `AUTH_MODE` | `legacy` forces the local email/password form (break-glass); anything else = CrimsonRaven-first. |

Hostnames/ids below are placeholders — the real values live only in the host `.env.*`
(never committed). Staging and prod point at **separate** CrimsonRaven instances with their
own user stores.

- **Local + staging** → e.g. `https://idp-staging.example.com` (client `<staging-client-id>`).
  HTTPS is required so the SPA's `crypto.subtle`/PKCE and Keycloak's session cookie work
  (secure context). Staging Fuel is served over HTTPS for the same reason.
- **Prod** → e.g. `https://idp.example.com` (separate instance). Prod Fuel is served at e.g.
  `https://app.example.com`. The Fuel app is registered as a **public PKCE client**
  (`auth_method = NONE`, no secret), same shape as staging:
  - `OIDC_CLIENT_ID == OIDC_AUDIENCE` (a single public client id).
  - redirect URI `https://app.example.com/auth/callback`; post-logout `https://app.example.com`.
  - Prod env (host `.env.prod`): `OIDC_AUTHORITY=https://idp.example.com`,
    `OIDC_CLIENT_ID`/`OIDC_AUDIENCE=<prod-client-id>`,
    `PUBLIC_BASE_URL=https://app.example.com` (see `deploy/.env.prod.example`).
  - Pre-flight (verified without deploying): discovery issuer matches authority, JWKS 200,
    `/authorize` accepts the app redirect (302) and rejects others (400).

## Verify

- `dotnet test backend/Fuel.slnx -c Release` — `OidcUserProvisionerTests` (link/verify/
  idempotency), `ResourceOwnershipFilter` still 403s cross-user, local JWT path intact.
- `npm test --prefix frontend -- --run` — SSO button visibility + redirect trigger.
- End-to-end (needs a real CrimsonRaven account with a verified, real email): SSO button →
  CrimsonRaven login → `/auth/callback` → day view shows that user's existing data;
  the legacy email/password login still works.

## Resolved during build

- **Access-token claims.** The access token doesn't carry `email`/`email_verified`, so
  `OidcUserProvisioner` falls back to a **userinfo lookup** (Keycloak's
  `{authority}/protocol/openid-connect/userinfo` with the request's bearer, via
  `IHttpContextAccessor`) to get the verified email for link-by-email.

## Open / later

- **Phase 4 cutover** (separate): remove `JwtTokenService`, password logic, `/api/auth/*`,
  the `"Fuel"` scheme, and drop `PasswordHash`.
