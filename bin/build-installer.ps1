<#
  Builds the shareable installer: dist\Shepherd-Markdown-Setup.exe

  Does the whole chain:
    1. recompiles ShepherdMD.exe from native\*.cs
    2. re-stages a CLEAN payload (app files only - no personal data)
    3. compiles installer\shepherd-md.iss with Inno Setup

  Requires Inno Setup 6 (winget install JRSoftware.InnoSetup).

  Usage:  .\bin\build-installer.ps1  [-Version 1.0.1]
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$App    = Split-Path -Parent $PSScriptRoot
$Stage  = Join-Path $App 'installer\payload'
$Iss    = Join-Path $App 'installer\shepherd-md.iss'
$Dist   = Join-Path $App 'dist'

# --- locate the Inno compiler ---
$Iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) { throw "Inno Setup not found. Install it:  winget install JRSoftware.InnoSetup" }

# --- 1. compile the app ---
Write-Host "[1/3] Compiling ShepherdMD.exe..." -ForegroundColor Cyan
$running = Get-Process ShepherdMD -ErrorAction SilentlyContinue
if ($running) {
  $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
  Start-Sleep -Milliseconds 1200
  $running | ForEach-Object { $_.Refresh(); if (-not $_.HasExited) { Stop-Process -Id $_.Id -Force } }
}
& (Join-Path $PSScriptRoot 'build-exe.ps1')

# --- 2. stage a clean payload ---
Write-Host "[2/3] Staging clean payload..." -ForegroundColor Cyan
if (Test-Path $Stage) { Remove-Item -LiteralPath $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Stage | Out-Null
New-Item -ItemType Directory -Force -Path $Dist  | Out-Null

$files = @('ShepherdMD.exe','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll',
           'WebView2Loader.dll','app.ico','favicon.ico','icon-192.png','icon-256.png','icon-512.png',
           'icon.svg','manifest.webmanifest')
foreach ($f in $files) { Copy-Item (Join-Path $App $f) -Destination $Stage -Force }
foreach ($d in 'public','lib')  { Copy-Item (Join-Path $App $d) -Destination $Stage -Recurse -Force }

# a fresh install must start with NO folders and NO session
$cleanConfig = @'
{
  "roots": []
}
'@
Set-Content -Path (Join-Path $Stage 'config.json') -Value $cleanConfig -Encoding UTF8
Copy-Item (Join-Path $App 'installer\README.dist.txt') -Destination (Join-Path $Stage 'README.txt') -Force

# safety net: never ship personal data
foreach ($bad in 'session.json','windowstate.txt','running.json','webview2profile','native','bin') {
  if (Test-Path (Join-Path $Stage $bad)) { throw "Payload contains '$bad' - refusing to build." }
}
$leak = Get-ChildItem $Stage -Recurse -File | Where-Object { $_.Length -lt 5MB } |
        Select-String -Pattern 'Work Stuff' -SimpleMatch -List -ErrorAction SilentlyContinue
if ($leak) { throw "Payload leaks a local path: $($leak.Path)" }

# --- 3. compile the installer ---
Write-Host "[3/3] Building installer..." -ForegroundColor Cyan
$isccArgs = @($Iss)
if ($Version) { $isccArgs = @("/DAppVersion=$Version", $Iss) }
& $Iscc @isccArgs | Select-Object -Last 4
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed (exit $LASTEXITCODE)" }

$out = Join-Path $Dist 'Shepherd-Markdown-Setup.exe'
Write-Host ""
Write-Host ("DONE -> {0} ({1:N1} MB)" -f $out, ((Get-Item $out).Length/1MB)) -ForegroundColor Green
Write-Host "Note: unsigned, so first-run shows SmartScreen (More info > Run anyway)." -ForegroundColor Yellow
