# Stable tenant identity (auth/IdP migrations)

Fuel keys all user data on an internal **`User.Id` (GUID)** that we own and never changes. The OIDC
`sub` and the email are *mutable external links*, mapped onto that id by `OidcUserProvisioner`
(`IClaimsTransformation`). This is the cross-app standard; the canonical write-up lives in the
MulberryHeron repo: **`docs/adr/0012-stable-tenant-identity.md`**.

## Login-time resolution (`OidcUserProvisioner`)
1. **Find by `ExternalSubject == sub`** → fast path for returning sessions.
2. Else resolve **email + email_verified** (token claims, else the IdP `userinfo` endpoint — Zitadel
   access tokens omit email).
3. **Verified** email matching an existing row → **link** (`ExternalSubject = sub`); the `User.Id`,
   and all its data, is preserved across the IdP move.
4. **Unverified** email that *already owns a row* → **HOLD**: persist nothing, leave the `sub`
   un-rewritten, and stamp a marker claim. `EmailVerificationHoldMiddleware` turns it into a clear
   **`403 {"error":"email_unverified","message":…}`** (the SPA can show a "verify your email" screen). A
   later *verified* login carries no marker → it's let through and self-heals into the row — no admin
   step, and an unverified address can never claim
   another person's data. (Replaced the old behaviour, which created a competing row that permanently
   shadowed the verified link, and the temporary `OIDC_LINK_ALLOW_UNVERIFIED_EMAIL` escape hatch.)
5. Else **create** a new password-less user.

**Primary guard is the IdP:** enforce email-verification-on-registration in Crimson Raven so step 4
rarely fires; the app-side hold is defense-in-depth.

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
