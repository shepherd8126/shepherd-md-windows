<#
  Removes the Shepherd MD Markdown-handler registration (HKCU only). Fully undoes register-default.ps1.
#>
$ErrorActionPreference = 'SilentlyContinue'
$ProgId = 'ShepherdMD.md'

Remove-Item -Path "HKCU:\Software\Classes\$ProgId" -Recurse -Force
foreach ($ext in '.md', '.markdown', '.mdown', '.mkd') {
  Remove-ItemProperty -Path "HKCU:\Software\Classes\$ext\OpenWithProgids" -Name $ProgId -Force
}
Remove-Item -Path 'HKCU:\Software\ShepherdMD' -Recurse -Force
Remove-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'Shepherd Markdown' -Force
Remove-ItemProperty -Path 'HKCU:\Software\RegisteredApplications' -Name 'Shepherd MD' -Force

Add-Type -Namespace Win32 -Name Shell -MemberDefinition '[DllImport("shell32.dll")] public static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);' -ErrorAction SilentlyContinue
try { [Win32.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) } catch {}
Write-Output 'Shepherd MD Markdown-handler registration removed.'
