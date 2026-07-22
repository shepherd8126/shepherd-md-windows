<#
  Registers Shepherd Markdown as a handler for .md / .markdown / .txt-style Markdown files.
  HKCU only (per-user, no admin, fully reversible via unregister-default.ps1).

  NOTE: On Windows 11 the *default* app for an extension is stored in a hash-protected,
  ACL-locked UserChoice key that applications cannot write. This script makes Shepherd Markdown
  a registered, first-class option; the final "set as default" is one click by the user
  (Settings > Default apps, or right-click a .md > Open with > Shepherd Markdown > Always).
#>
$ErrorActionPreference = 'Stop'
$AppDir = Split-Path -Parent $PSScriptRoot
$Exe = Join-Path $AppDir 'ShepherdMD.exe'
$Ico = $Exe
$ProgId = 'ShepherdMD.md'
$cmd = '"' + $Exe + '" "%1"'

function SetKey($path, $name, $value) {
  if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
  New-ItemProperty -Path $path -Name $name -Value $value -PropertyType String -Force | Out-Null
}

# ProgID
SetKey "HKCU:\Software\Classes\$ProgId" '(default)' 'Markdown Document'
SetKey "HKCU:\Software\Classes\$ProgId\DefaultIcon" '(default)' "$Ico,0"
SetKey "HKCU:\Software\Classes\$ProgId\shell\open" 'FriendlyAppName' 'Shepherd Markdown'
SetKey "HKCU:\Software\Classes\$ProgId\shell\open\command" '(default)' $cmd

# make it an "Open with" option for common markdown extensions
foreach ($ext in '.md', '.markdown', '.mdown', '.mkd') {
  if (-not (Test-Path "HKCU:\Software\Classes\$ext\OpenWithProgids")) { New-Item -Path "HKCU:\Software\Classes\$ext\OpenWithProgids" -Force | Out-Null }
  New-ItemProperty -Path "HKCU:\Software\Classes\$ext\OpenWithProgids" -Name $ProgId -Value ([byte[]]@()) -PropertyType None -Force | Out-Null
}

# Capabilities -> appears in Settings > Default apps
SetKey 'HKCU:\Software\ShepherdMD\Capabilities' 'ApplicationName' 'Shepherd Markdown'
SetKey 'HKCU:\Software\ShepherdMD\Capabilities' 'ApplicationDescription' 'A modern local reader for Markdown files.'
SetKey 'HKCU:\Software\ShepherdMD\Capabilities' 'ApplicationIcon' "$Ico,0"
foreach ($ext in '.md', '.markdown', '.mdown', '.mkd') { SetKey 'HKCU:\Software\ShepherdMD\Capabilities\FileAssociations' $ext $ProgId }
SetKey 'HKCU:\Software\RegisteredApplications' 'Shepherd Markdown' 'Software\ShepherdMD\Capabilities'

# refresh shell association cache
Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);' -ErrorAction SilentlyContinue
try { [Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) } catch {}

Write-Output 'Shepherd Markdown registered as a Markdown handler (HKCU).'
