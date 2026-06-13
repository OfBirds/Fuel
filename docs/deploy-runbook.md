# Deploy runbook (homelab)

One-time setup of the Debian VM `source` (`vm.example.lan`) so that pushes to
`main`/`release` auto-deploy via the GitHub Actions workflow
(`.github/workflows/deploy.yml`). Run everything below **on the VM** as a normal
sudo-capable user.

## 1. Docker present

```bash
docker version            # daemon reachable?
docker compose version    # v2 compose plugin present?
```

If missing, install Docker Engine + the compose plugin (get.docker.com), then:

```bash
sudo usermod -aG docker "$USER"   # run docker without sudo
# log out / back in for the group to take effect
```

## 2. Runtime env files (hold the real secrets, never in git)

```bash
sudo mkdir -p /opt/fuel
sudo chown "$USER" /opt/fuel
```

Create `/opt/fuel/.env.staging` and `/opt/fuel/.env.prod` using the
committed templates in `deploy/.env.staging.example` / `.env.prod.example` as the
shape. Fill in **real** DB passwords. The workflow references these absolute
paths. Minimum contents:

```
# /opt/fuel/.env.staging
COMPOSE_PROJECT_NAME=fuel-staging
APP_IMAGE=ghcr.io/trifunovich/fuel:staging
APP_PORT=9123
DB_HOST_PORT=5433
DB_NAME=fuel
DB_USER=fuel
DB_PASSWORD=<real-staging-password>
```

```
# /opt/fuel/.env.prod
COMPOSE_PROJECT_NAME=fuel-prod
APP_IMAGE=ghcr.io/trifunovich/fuel:prod
APP_PORT=9124
DB_HOST_PORT=5434
DB_NAME=fuel
DB_USER=fuel
DB_PASSWORD=<real-prod-password>
```

```bash
chmod 600 /opt/fuel/.env.*
```

## 3. Self-hosted GitHub Actions runner

On GitHub: **repo → Settings → Actions → Runners → New self-hosted runner →
Linux / x64**. That page shows a download block and a `./config.sh` line with a
**registration token** (short-lived). Use that page's exact version/token, but
with the customizations below.

```bash
mkdir -p ~/actions-runner && cd ~/actions-runner
# (use the download/extract lines GitHub shows for the current runner version)

# Configure — note the custom label `source`, which the workflow targets:
./config.sh \
  --url https://github.com/trifunovich/Fuel \
  --token <REGISTRATION_TOKEN_FROM_GITHUB> \
  --name source \
  --labels source \
  --unattended

# Install + start as a systemd service so it survives reboots:
sudo ./svc.sh install
sudo ./svc.sh start
sudo ./svc.sh status
```

The runner connects outbound to GitHub — no inbound ports opened.

> The runner runs `docker compose`, so the runner's user must be in the `docker`
> group (step 1). If you installed the service before adding the group, restart
> it: `sudo ./svc.sh stop && sudo ./svc.sh start`.

## 4. First deploy

Push to `main` → the workflow builds the image, pushes to GHCR, the runner pulls,
migrates, and brings up the staging stack. Then verify on the LAN:

- staging app:  `http://vm.example.lan:9123/`
- staging DB (DBeaver): `vm.example.lan:5433`

For prod, create the `release` branch and push it:

```bash
git checkout -b release && git push -u origin release
```

- prod app: `http://vm.example.lan:9124/`
- prod DB (DBeaver): `vm.example.lan:5434`

## Day-to-day

- Deploys are automatic on push to `main`/`release`.
- Manual stack control on the VM:
  ```bash
  cd <repo>/deploy   # or wherever the runner checked it out
  docker compose --env-file /opt/fuel/.env.staging ps
  docker compose --env-file /opt/fuel/.env.staging logs -f app
  ```
- Images are tagged `:staging`/`:prod` (moving) and `:<sha>` (immutable) in GHCR,
  so rollback = point the env file's `APP_IMAGE` at a known `:<sha>` and re-run
  `up -d`.
