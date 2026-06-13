<#
.SYNOPSIS
  Rename this template's placeholder tokens to your project's identity.

.DESCRIPTION
  Replaces, across the repo:
    AppName      -> <AppPascalCase>   (brand: Serilog/OTel name, solution file,
                                        email body, PWA manifest, README, docs, UI)
    appname      -> <app-slug>        (lowercase: db name/user, GHCR image,
                                        compose project, container names, /opt path,
                                        sw cache, package name)
    your-org     -> <GithubOwner>     (GHCR owner / GitHub org in CI + docs)
    AppName.slnx -> <AppPascalCase>.slnx (file is renamed too)

  .NET project folders/namespaces stay `Api` / `Api.Tests` by design.

.EXAMPLE
  ./scripts/rename.ps1 MyApp my-github-org
.EXAMPLE
  ./scripts/rename.ps1 MyApp my-github-org -Db myappdb -Ports 9201,9202
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true, Position = 0)][string]$App,
  [Parameter(Mandatory = $true, Position = 1)][string]$Owner,
  [string]$Db,
  [string]$Ports
)

$ErrorActionPreference = 'Stop'

$slug = $App.ToLowerInvariant()
if (-not $Db) { $Db = $slug }

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

Write-Host "Renaming template -> App='$App' slug='$slug' owner='$Owner' db='$Db'"

$exclude = @('\.git\', '\scripts\', '\node_modules\', '\bin\', '\obj\', '\dist\')

$files = Get-ChildItem -Recurse -File | Where-Object {
  $p = $_.FullName
  -not ($exclude | Where-Object { $p -like "*$_*" })
}

foreach ($f in $files) {
  # Skip binary files (NUL byte heuristic).
  $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
  if ($bytes -contains 0) { continue }

  $text = [System.IO.File]::ReadAllText($f.FullName)
  $orig = $text
  $text = $text -replace 'AppName\.slnx', "$App.slnx"
  $text = $text -replace 'AppName', $App
  $text = $text -replace 'your-org', $Owner
  $text = $text -replace 'appname', $slug
  if ($text -ne $orig) {
    [System.IO.File]::WriteAllText($f.FullName, $text)
  }
}

# Rename the solution file.
$sln = Join-Path $root 'backend/AppName.slnx'
if (Test-Path $sln) { Rename-Item $sln "$App.slnx" }

# Optional: distinct DB name (otherwise it equals the slug, already applied).
if ($Db -ne $slug) {
  $repl = @{
    'backend/Api/appsettings.json' = @{ "`"DB_NAME`": `"$slug`"" = "`"DB_NAME`": `"$Db`"" }
    'backend/Api/Program.cs'       = @{ "?? `"$slug`"" = "?? `"$Db`"" }
    'docker-compose.yml'           = @{ "`${DB_NAME:-$slug}" = "`${DB_NAME:-$Db}" }
  }
  foreach ($file in $repl.Keys) {
    $t = [System.IO.File]::ReadAllText($file)
    foreach ($k in $repl[$file].Keys) { $t = $t.Replace($k, $repl[$file][$k]) }
    [System.IO.File]::WriteAllText($file, $t)
  }
  foreach ($env in 'deploy/.env.staging.example', 'deploy/.env.prod.example') {
    $t = [System.IO.File]::ReadAllText($env)
    $t = $t -replace "(?m)^DB_NAME=$slug$", "DB_NAME=$Db"
    $t = $t -replace "(?m)^DB_USER=$slug$", "DB_USER=$Db"
    [System.IO.File]::WriteAllText($env, $t)
  }
}

# Optional: remap the staging/prod app ports (defaults 9123 / 9124).
if ($Ports) {
  $stg, $prd = $Ports.Split(',')
  foreach ($file in @('deploy/.env.staging.example') + (Get-ChildItem docs/*.md).FullName) {
    (Get-Content $file -Raw).Replace('9123', $stg) | Set-Content $file -NoNewline
  }
  foreach ($file in @('deploy/.env.prod.example') + (Get-ChildItem docs/*.md).FullName) {
    (Get-Content $file -Raw).Replace('9124', $prd) | Set-Content $file -NoNewline
  }
}

Write-Host "Done. Review the diff (git diff), then commit."
Write-Host "Reminder: harden auth before deploying — see README 'Before you ship'."
