# Run on Windows with .NET 8 SDK + Inno Setup 6.
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
Remove-Item publish, dist -Recurse -Force -ErrorAction SilentlyContinue
# Self-contained so the child's PC needs no runtime install; both apps share one folder.
dotnet publish Agent.Service -c Release -r win-x64 --self-contained true -o publish
dotnet publish Agent.Helper  -c Release -r win-x64 --self-contained true -o publish
$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
& $iscc installer\KidSafe.iss
Write-Host "Installer: $PWD\dist\KidSafeSetup.exe"
