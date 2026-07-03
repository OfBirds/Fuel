# Deploy runbook — push-to-deploy CI/CD

> **Not the self-hosting guide.** This documents a *continuous-deployment* rig — a
> self-hosted GitHub Actions runner on a Linux VM that auto-deploys on every push to
> `main`/`release`. **To run Indigo Swallow yourself you don't need any of this**; see
> the [self-hosting guide](modules/ROOT/pages/self-hosting.adoc) (`docker compose up`).
> This page is for contributors reproducing the CI/CD setup.

Host setup on a Linux VM (`vm.example.lan` — substitute your VM's real LAN address) so
pushes to `main`/`release` auto-deploy via `.github/workflows/deploy.yml`. Run on the VM
as a sudo-capable user. The app gets its **own** runner instance and its **own** ports so
it can coexist with other apps on the same host.

## 1. Docker present

```bash
docker version            # daemon reachable?
docker compose version    # v2 compose plugin present?
```
If missing, install Docker Engine + the compose plugin (get.docker.com), then add
the runner's user to the `docker` group:
```bash
sudo usermod -aG docker at   # the runner runs as 'at'; re-login for it to take effect
```

## 2. Runtime env files (real secrets, never in git)

```bash
sudo mkdir -p /opt/fuel && sudo chown at:at /opt/fuel
```
Create `/opt/fuel/.env.staging` and `/opt/fuel/.env.prod` from the committed
templates `deploy/.env.staging.example` / `.env.prod.example`. Fill in **real** DB
passwords (and, for prod, `SMTP_*` + a real `PUBLIC_BASE_URL`). The workflow reads
these absolute paths. Ports are already Fuel's deconflicted block:

```
# /opt/fuel/.env.staging
COMPOSE_PROJECT_NAME=fuel-staging
APP_IMAGE=ghcr.io/ofbirds/fuel:staging
APP_PORT=9223
DB_HOST_PORT=5435
SEQ_PORT=9233
DB_NAME=fuel
DB_USER=fuel
DB_PASSWORD=<real-staging-password>
```
```
# /opt/fuel/.env.prod
COMPOSE_PROJECT_NAME=fuel-prod
APP_IMAGE=ghcr.io/ofbirds/fuel:prod
APP_PORT=9224
DB_HOST_PORT=5436
SEQ_PORT=9234
DB_NAME=fuel
DB_USER=fuel
DB_PASSWORD=<real-prod-password>
```
```bash
chmod 600 /opt/fuel/.env.*
```

The runner's user also needs to pull from GHCR: `docker login ghcr.io` as `at`
(once; reused across apps).

## 3. Self-hosted GitHub Actions runner (Fuel's own instance)

⚠️ Use a **dedicated folder** — do not reconfigure another app's runner folder.

On GitHub: **Fuel repo → Settings → Actions → Runners → New self-hosted runner →
Linux / x64** for a short-lived **registration token**.

```bash
# as 'at' — own folder so other runners are untouched:
mkdir -p ~/actions-runner-fuel && cd ~/actions-runner-fuel
# extract the runner (reuse an existing actions-runner tarball if present)
tar xzf ~/actions-runner/actions-runner-linux-x64-2.334.0.tar.gz

./config.sh \
  --url https://github.com/OfBirds/Fuel \
  --token <REGISTRATION_TOKEN_FROM_GITHUB> \
  --name fuel \
  --unattended --replace

# install + start as a systemd service so it survives reboots:
sudo ./svc.sh install at
sudo ./svc.sh start
sudo ./svc.sh status
```
Fuel's workflow targets `runs-on: [self-hosted]` (no custom label needed — the
runner is repo-scoped to Fuel). The runner connects outbound only; no inbound
ports. If `config.sh` returns `404 Not Found`, the token expired — get a fresh one.

## 4. First deploy

Push to `main` → the workflow tests, builds, pushes to GHCR, applies the migration
script, and the runner brings up the staging stack. Verify on the LAN:
- staging app: `http://vm.example.lan:9223/`
- staging DB (DBeaver): `vm.example.lan:5435`
- staging Seq UI: `http://vm.example.lan:9233/`

For prod, create and push `release`:
```bash
git checkout -b release && git push -u origin release
```
- prod app: `http://vm.example.lan:9224/` · DB `vm.example.lan:5436` · Seq `:9234`

## Day-to-day

- Deploys are automatic on push to `main`/`release`.
- Manual stack control on the VM:
  ```bash
  cd <repo>/deploy   # or wherever the runner checked it out
  docker compose --env-file /opt/fuel/.env.staging ps
  docker compose --env-file /opt/fuel/.env.staging logs -f app
  ```
- Images are tagged `:staging`/`:prod` (moving) and `:<sha>` (immutable) in GHCR —
  rollback = point the env file's `APP_IMAGE` at a known `:<sha>` and re-run
  `up -d`.
