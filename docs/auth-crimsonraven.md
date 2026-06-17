# Auth — CrimsonRaven SSO (dual-auth, data-preserving)

> Status: **built** (dual-auth). Fuel accepts **both** CrimsonRaven (OIDC) tokens **and**
> its own legacy HMAC JWT, so the existing email/password login keeps working as the
> backup during migration. Decommissioning the local path is a deliberate later step.

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
- **`GET /api/config`** (anonymous) exposes `{ oidcEnabled, oidcAuthority, oidcClientId }`
  so the single Docker image can be configured per-stack at runtime (no rebuild).

## Frontend

- `oidc-client-ts` `UserManager`, configured at runtime from `/api/config`
  (`src/lib/oidc.ts`). Authorization Code + PKCE, `scope "openid profile email
  offline_access"`, silent renew via refresh token.
- `AuthContext` gains `loginWithSSO()` (redirect) and `completeSsoCallback()`; the
  `/auth/callback` route (`AuthCallbackPage`) finishes the code exchange, calls
  `/api/auth/me`, and stores `token` + `user {id,email}` the **same** way as the local
  path — so `apiFetch`'s Bearer attach and 401→clear are unchanged.
- `LoginPage` shows **both**: a "Continue with CrimsonRaven" button (when `ssoEnabled`)
  and the existing email/password form.

## Config (flat env, per stack)

| Key | Meaning |
|---|---|
| `OIDC_AUTHORITY` | IdP issuer; must equal the token `iss`. Blank → SSO off. |
| `OIDC_CLIENT_ID` | Fuel public/PKCE client id in CrimsonRaven. |
| `OIDC_AUDIENCE` | Expected `aud` in the access token. |

- **Local + staging** → `http://192.168.4.55:9100` (homelab Zitadel, client
  `377800190771331075`).
- **Prod** → `https://raven.bearsoft.duckdns.org` (separate instance; its own Fuel app +
  user store).

## Verify

- `dotnet test backend/Fuel.slnx -c Release` — `OidcUserProvisionerTests` (link/verify/
  idempotency), `ResourceOwnershipFilter` still 403s cross-user, local JWT path intact.
- `npm test --prefix frontend -- --run` — SSO button visibility + redirect trigger.
- End-to-end (needs a real CrimsonRaven account with a verified, real email): SSO button →
  CrimsonRaven login → `/auth/callback` → day view shows that user's existing data;
  the legacy email/password login still works.

## Open / later

- **Confirm `aud` + access-token claims** against a real Zitadel token (set the Fuel app's
  access-token type to **JWT**; verify `email` / `email_verified` are present — if the
  access token omits them, add a userinfo lookup or a Zitadel Action).
- **Phase 4 cutover** (separate): remove `JwtTokenService`, password logic, `/api/auth/*`,
  the `"Fuel"` scheme, and drop `PasswordHash`.
