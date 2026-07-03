#!/usr/bin/env bash
#
# Rename this template's placeholder tokens to your project's identity.
#
# Usage:
#   ./scripts/rename.sh <AppPascalCase> <github-owner> [--db <name>] [--ports <staging>,<prod>]
#
# Examples:
#   ./scripts/rename.sh MyApp my-github-org
#   ./scripts/rename.sh MyApp my-github-org --db myappdb --ports 9201,9202
#
# Replaces, across the repo:
#   AppName      -> <AppPascalCase>   (brand: Serilog/OTel name, solution file,
#                                       email body, PWA manifest, README, docs, UI)
#   appname      -> <app-slug>        (lowercase: db name/user, GHCR image,
#                                       compose project, container names, /opt path,
#                                       sw cache, package name)
#   your-org     -> <github-owner>    (GHCR owner / GitHub org in CI + docs)
#   AppName.slnx -> <AppPascalCase>.slnx (file is renamed too)
#
# .NET project folders/namespaces stay `Api` / `Api.Tests` by design.
set -euo pipefail

usage() {
  grep '^#' "$0" | sed 's/^# \{0,1\}//' | sed '/^!/d'
  exit "${1:-0}"
}

[ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ] && usage 0
[ $# -lt 2 ] && { echo "error: need <AppPascalCase> and <github-owner>" >&2; usage 1; }

APP="$1"; OWNER="$2"; shift 2
SLUG="$(printf '%s' "$APP" | tr '[:upper:]' '[:lower:]')"
DB="$SLUG"
PORTS=""

while [ $# -gt 0 ]; do
  case "$1" in
    --db)    DB="$2"; shift 2 ;;
    --ports) PORTS="$2"; shift 2 ;;
    *) echo "error: unknown arg '$1'" >&2; usage 1 ;;
  esac
done

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

echo "Renaming template -> App='$APP' slug='$SLUG' owner='$OWNER' db='$DB'"

# Files to touch: all text files, excluding VCS/build/deps and this scripts dir.
mapfile -d '' FILES < <(
  find . -type f \
    -not -path './.git/*' \
    -not -path './scripts/*' \
    -not -path '*/node_modules/*' \
    -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/dist/*' \
    -print0
)

for f in "${FILES[@]}"; do
  # Skip binary files.
  if grep -Iq . "$f" 2>/dev/null; then
    sed -i \
      -e "s/AppName\.slnx/${APP}.slnx/g" \
      -e "s/AppName/${APP}/g" \
      -e "s/your-org/${OWNER}/g" \
      -e "s/appname/${SLUG}/g" \
      "$f"
  fi
done

# Rename the solution file.
if [ -f "backend/AppName.slnx" ]; then
  mv "backend/AppName.slnx" "backend/${APP}.slnx"
fi

# Optional: distinct DB name (otherwise it equals the slug, already applied).
if [ "$DB" != "$SLUG" ]; then
  sed -i "s/\"DB_NAME\": \"${SLUG}\"/\"DB_NAME\": \"${DB}\"/" backend/Api/appsettings.json
  sed -i "s/?? \"${SLUG}\"/?? \"${DB}\"/" backend/Api/Program.cs
  sed -i "s/\${DB_NAME:-${SLUG}}/\${DB_NAME:-${DB}}/" docker-compose.yml
  sed -i "s/^DB_NAME=${SLUG}$/DB_NAME=${DB}/;s/^DB_USER=${SLUG}$/DB_USER=${DB}/" \
    deploy/.env.staging.example deploy/.env.prod.example
fi

# Optional: remap the staging/prod app ports (defaults 9123 / 9124).
if [ -n "$PORTS" ]; then
  STG="${PORTS%%,*}"; PRD="${PORTS##*,}"
  sed -i "s/9123/${STG}/g" deploy/.env.staging.example docs/*.md
  sed -i "s/9124/${PRD}/g" deploy/.env.prod.example docs/*.md
fi

echo "Done. Review the diff (git diff), then commit."
echo "Reminder: harden auth before deploying — see README 'Before you ship'."
