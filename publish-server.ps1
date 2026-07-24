<#
  Publishes the licence server as a single self-contained Windows exe for the VPS.

  Output (default dist-server/): DataVortex.LicenseServer.exe + appsettings.json + wwwroot/ (admin dashboard).
  On the VPS you add appsettings.Production.json + server-tls.pfx (from _secrets/) next to the exe, then run
  install-service.ps1. The server is NOT obfuscated (it never leaves the VPS; only the client is distributed).

  Usage:  pwsh ./publish-server.ps1 [-Output dist-server] [-Version 1.0.0]
#>
param(
  [string]$Output  = "dist-server",
  [string]$Version = "1.0.0"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

Write-Host "== publishing licence server (self-contained single-file win-x64) ==" -ForegroundColor Cyan
dotnet publish src/DataVortex.LicenseServer/DataVortex.LicenseServer.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false -p:Version=$Version -o $Output -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

Write-Host "OK -> $Output\DataVortex.LicenseServer.exe" -ForegroundColor Green
Write-Host "Next: copy $Output\ to the VPS (e.g. C:\DataVortex\), add appsettings.Production.json + server-tls.pfx from _secrets\, then run install-service.ps1." -ForegroundColor DarkGray
