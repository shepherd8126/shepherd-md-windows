<#
  Rebuilds ShepherdMD.exe from native/Program.cs using the in-box .NET Framework
  C# compiler (no .NET SDK required). Requires the WebView2 SDK DLLs to already be
  in the app root (Microsoft.Web.WebView2.Core.dll, .WinForms.dll, WebView2Loader.dll).
#>
$ErrorActionPreference = 'Stop'
$App = Split-Path -Parent $PSScriptRoot
$csc = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$args = @(
  '/nologo', '/target:winexe', '/platform:x64',
  "/out:$App\ShepherdMD.exe",
  "/win32icon:$App\app.ico",
  "/reference:$App\Microsoft.Web.WebView2.Core.dll",
  "/reference:$App\Microsoft.Web.WebView2.WinForms.dll",
  '/reference:System.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Core.dll',
  "$App\native\Program.cs", "$App\native\Server.cs"
)
& $csc $args
if ($LASTEXITCODE -eq 0 -and (Test-Path "$App\ShepherdMD.exe")) {
  Write-Output ("Built ShepherdMD.exe ({0} KB)" -f [math]::Round((Get-Item "$App\ShepherdMD.exe").Length / 1KB))
} else {
  throw "Build failed (exit $LASTEXITCODE)"
}
