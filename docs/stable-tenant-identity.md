# Stable tenant identity (auth/IdP migrations)

Fuel keys all user data on an internal **`User.Id` (GUID)** that we own and never changes. The OIDC
`sub` and the email are *mutable external links*, mapped onto that id by `OidcUserProvisioner`
(`IClaimsTransformation`). This is the cross-app standard across the ecosystem (the canonical
write-up lives in a private repo ADR).

## Login-time resolution (`OidcUserProvisioner`)
1. **Find by `ExternalSubject == sub`** → fast path for returning sessions.
2. Else resolve **email + email_verified** (token claims, else the IdP `userinfo` endpoint — Zitadel
   access tokens omit email).
3. Email matching an existing row → **link** (`ExternalSubject = sub`); the `User.Id`,
   and all its data, is preserved across the IdP move.
4. Else **create** a new password-less user.

**Email verification is the IdP's job.** The IdP (Keycloak) enforces
email-verification-on-registration, so a token only ever carries a verified address and the
provisioner can link by email unconditionally. There is no app-side HOLD/`403 email_unverified`
gate — an earlier `EmailVerificationHoldMiddleware` (and the temporary
`OIDC_LINK_ALLOW_UNVERIFIED_EMAIL` escape hatch) were removed once the IdP owned verification.

## Migrating to a new IdP/instance — deterministic, workaround-free
The non-negotiable rule: **never** disable email verification or force a mass re-login to "fix"
linking. Instead, an admin **pre-seeds the identity map** so every login hits the fast
`ExternalSubject` path immediately:

1. Export `(email, new_sub)` for every user from the **new** IdP instance.
2. Backfill by verified email (idempotent; run inside a transaction):
   ```sql
   -- for each (email, new_sub) from the new IdP export:
   UPDATE "Users"
      SET "ExternalSubject" = @new_sub
    WHERE lower("Email") = lower(@email)
      AND "ExternalSubject" IS DISTINCT FROM @new_sub;
   ```
3. After the cutover the first login matches on `ExternalSubject` — no dependence on login-time
   email verification, no relog, no duplicate accounts.

Because all apps now share one Crimson Raven instance, `sub` is stable going forward; this is the
procedure for any *future* move, not an ongoing concern.
