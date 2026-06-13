# Release notifications — design

> Status: **implemented** (see the ✅ items in §8). Documents the design and the
> as-built behaviour for emailing registered users when a new version is released
> to prod. The remaining open item is the prod public address / reverse proxy
> (§5), handled with the deployment pipeline.

## Goal

When a new version is deployed to **prod**, automatically email every registered
(opted-in) user: "a new version of Fuel is out — check it at <link>". The
behaviour is gated by a flag so staging never emails, and prod does. This is
admin-controlled in the sense that flipping the flag (per environment) decides
whether the broadcast happens; once on, it fires automatically on each new
version.

Two prerequisites drive everything: **versioning** (so "new version" is
meaningful and we can avoid re-sending) and a **stable public address** (so the
link in the email keeps working).

## 1. Versioning

Today there is no real versioning: backend has no version, frontend is a static
`1.0.0`, no git tags, no `/version` endpoint. We add an **automatic** scheme — no
manual tagging:

- **You control `MAJOR.MINOR`, the pipeline auto-appends the patch.**
  - A `VERSION` file at the repo root holds just `MAJOR.MINOR` (e.g. `1.0`). It's
    the only thing edited by hand: bump to `1.1` for a new revision, `2.0` for a
    new release.
  - The pipeline computes the patch from **`github.run_number`** (GitHub's
    built-in monotonic per-workflow counter) and assembles the full version:
    `1.0.<run_number>` → e.g. `1.0.47`. No git tags, no write-back to the repo,
    no state to manage.
  - Patch is **non-resetting** (simple): bumping the minor gives `1.1.48`, not
    `1.1.0`. Still unique and monotonic — fine as an idempotency key and for
    "new version" messaging.
- **Inject at build time** into the image:
  - Backend: pass `-p:Version` / `-p:InformationalVersion` to `dotnet publish`
    (via a Docker `ARG APP_VERSION`). Exposed at runtime through the assembly's
    informational version.
  - Frontend: a Vite `define` (e.g. `__APP_VERSION__`) from a build env var, so
    the running client knows its own version.
  - Image tag: add `:<version>` alongside the existing `:prod`/`:sha` tags.
- **`GET /api/version`** returns `{ version, commit, builtAt }` — the canonical
  way anything asks "what version is the server?".

The version string is the **idempotency key** for notifications (below), which
is why this has to exist first.

## 2. The notification trigger

**The app sends the emails itself, on startup** — not the pipeline. The app owns
the user list and the email transport, and "app has started" already means
"deploy + migrations succeeded", so startup is the natural post-deploy hook.

### Flow (on application startup, in a background task)

```
if config.NOTIFY_ON_DEPLOY == true
   and currentVersion != state.last_notified_version:
       recipients = users where notify_releases == true
       for each recipient: send release email (async, best-effort)
       state.last_notified_version = currentVersion   # persist
```

- **Idempotent**: `last_notified_version` is stored in the DB, so restarts /
  redeploys of the *same* version never re-send. Emails go out once per new
  version.
- **Gated**: staging runs with `NOTIFY_ON_DEPLOY=false`, so it exercises the
  whole build/version path without ever emailing. Prod runs with `true`.
- **Non-blocking**: runs in a background task (`IHostedService` /
  `ApplicationStarted`), never blocking startup; a mail failure logs and does not
  crash the app.
- `last_notified_version` is set once the batch has been attempted, to avoid a
  resend storm if one address fails. Per-recipient failures are logged for
  manual follow-up (retry/queue can come later).

## 3. Email transport

Outbound mail via **MailKit** (modern .NET SMTP library; the legacy `SmtpClient`
is deprecated). The sender is **Gmail SMTP with an app password**.

- Host/port: `smtp.gmail.com:587`, STARTTLS → `SecureSocketOptions.StartTls`
  (the sender picks the mode from the port: 465→SslOnConnect, 587→StartTls).
- Auth uses a **Google App Password** (16 chars), which requires 2-Step
  Verification on the account. The normal Google password will not work for SMTP.
- Volume is trivial (a few/week), far under Gmail's limits.

> History / lesson: the original plan was the SBB ISP mailbox (`smtp.sbb.rs`),
> but that hostname is **NXDOMAIN** (dead) — `@sbb.rs` mail actually lives on
> `sbb.nlnet.rs`. Even pointed there, SBB rejected the mailbox password
> (`535 5.7.8`), and the account's auth had broken (Gmail's fetch of it failed
> too). Gmail SMTP + app password was the clean, reliable substitute. Because
> the transport is pure config, the switch was just env-file edits — no code
> change. Seq made each failure mode (DNS → wrong host → auth) obvious.

### Config keys

Non-secret (env files, committed as `.example`):

| Key | Staging | Prod |
|---|---|---|
| `NOTIFY_ON_DEPLOY` | `false` | `true` |
| `PUBLIC_BASE_URL` | `http://vm.example.lan:9123` | `https://app.example.com` |
| `SMTP_HOST` | (blank) | `smtp.gmail.com` |
| `SMTP_PORT` | `587` | `587` |
| `SMTP_FROM` / `SMTP_USER` | (blank) | `…@gmail.com` |
| `SMTP_ACCEPT_ALL_CERTS` | `true` | `false` (Gmail cert is valid) |
| `SEQ_PORT` | `9133` | `9134` |

Secret (the SMTP app password) lives **only** in `/opt/<app>/.env.prod` on the
server, `chmod 600`, never committed — same handling as the DB password.

## 4. Recipients & opt-out

- Recipients = users with release notifications enabled.
- Add to `Users`: `notify_releases bool default true` and an
  `unsubscribe_token` (GUID) for one-click opt-out.
- Email includes an **unsubscribe link**: `PUBLIC_BASE_URL/api/unsubscribe?token=…`
  which flips `notify_releases` to false. Basic hygiene; keeps us off spam lists.

## 5. Public address

- The email link is built from `PUBLIC_BASE_URL` (config), never a hardcoded IP —
  so internal IP changes don't break old links, and moving to a real domain later
  is a one-line env change.
- Prod is reached via **DuckDNS + nginx reverse proxy**:
  `https://app.example.com` → nginx (TLS termination, Let's
  Encrypt via DuckDNS DNS-01) → app container over plain HTTP on the LAN.
- TLS at the edge, HTTP inside the LAN is the standard, fine arrangement.
- Not going public/large — a handful of known users. (If they were all willing to
  run Tailscale, public exposure could be avoided entirely; chosen path is the
  public DuckDNS link so any device can just click through.)

## 5b. Observability (Seq)

The stack runs a [Seq](https://datalust.co/seq) service and the app ships
structured logs to it (`SEQ_URL=http://seq`), so the release send is reviewable.
The notifier emits, all tagged with `Version`:

- `Release announcement starting … {Recipients} opted-in user(s), link {Link}`
- per recipient: `Release email sent to {Recipient}` / `Release email FAILED …`
- `Release announcement complete … {Sent} sent, {Failed} failed of {Total}`

So in Seq you can filter by `Version` and see exactly who received the email and
what failed. UI on the LAN at `SEQ_PORT` (staging `9133`, prod `9134`).

## 6. Data model changes (new migration)

- `Users`: `notify_releases bool not null default true`, `unsubscribe_token uuid`.
- New single-row `SystemSettings` (or `NotificationState`) table holding
  `last_notified_version text`.

## 7. Open decisions

1. **Versioning** — DECIDED: `VERSION` file (`MAJOR.MINOR`) + auto patch from
   `github.run_number`, non-resetting. No manual tags.
2. **`last_notified_version` storage** — dedicated tiny table vs a general
   key/value `SystemSettings`. Leaning key/value so future small settings reuse it.
3. **Email body** — plain text vs simple HTML. Start with simple HTML + text
   fallback.
4. **Unsubscribe UX** — bare endpoint that flips the flag and returns a small
   confirmation, vs a frontend page. Start with the endpoint.

## 8. Implementation order

1. ✅ **Versioning** (done): `VERSION` file; CI builds `MAJOR.MINOR.<run_number>`
   and passes it via Dockerfile `ARG APP_VERSION`/`GIT_COMMIT`/`BUILD_TIME` →
   backend `InformationalVersion` + `GET /api/version`, frontend `__APP_VERSION__`
   define; image tagged `:<version>` too.
2. ✅ **Data model** (done): migration `AddNotificationsAndSettings` — `Users`
   `NotifyReleases` (default true) + `UnsubscribeToken` (`gen_random_uuid()`,
   backfills existing rows) + unique index; new `SystemSettings` key/value table.
3. ✅ **Email service** (done): MailKit `SmtpEmailSender` (`IEmailSender`),
   `SmtpOptions` bound from `SMTP_*` env keys, port-based TLS (465→SslOnConnect),
   accept-all-certs callback for SBB. Live send verified at deploy.
4. ✅ **Startup notifier** (done): `ReleaseNotifier` (`BackgroundService`) — flag
   + version check, opted-in recipients, per-user best-effort send, records
   `last_notified_version`. Flow/idempotency verified (flag-off idle, announce,
   skip-on-restart).
5. ✅ **Unsubscribe** (done): `GET /api/unsubscribe?token=…` flips
   `NotifyReleases` off (200/404/400 verified).
6. ✅ **Env wiring** (done): compose `app` service passes
   `NOTIFY_ON_DEPLOY`/`PUBLIC_BASE_URL`/`SMTP_*`; keys added to both
   `.env.*.example`. Real `/opt/fuel/.env.prod` on the server still needs the
   keys + real `SMTP_PASS` (manual, alongside step 7).
7. **DuckDNS + nginx** in front of prod; set `PUBLIC_BASE_URL` accordingly.
8. Verify on staging (flag off → no mail, but `/api/version` correct), then tag a
   release and confirm exactly one email per user on prod.
