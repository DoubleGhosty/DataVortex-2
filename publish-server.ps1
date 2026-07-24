<#
  Builds the licence server as a TURNKEY, ready-to-run bundle for the VPS.

  Output (default dist-server/):
    DataVortex.LicenseServer.exe   self-contained single-file (embedded SQLite - NO database to install)
    wwwroot/                       admin dashboard
    appsettings.json               non-secret defaults
    appsettings.Production.json    filled secrets (signing key, HMAC, TLS cert password)  [from _secrets/]
    server-tls.pfx                 TLS cert                                                [from _secrets/]

  Copy the whole folder to the VPS and run the exe - it creates its SQLite file on first launch and prints the
  generated admin password + TOTP. Nothing else to provision. The server is NOT obfuscated (it never leaves your
  VPS; only the client is distributed) and must NEVER go in a public release (it carries your signing key).

  Usage:  pwsh ./publish-server.ps1 [-Output dist-server] [-Version 1.0.0] [-SecretsDir _secrets]
#>
param(
  [string]$Output     = "dist-server",
  [string]$Version    = "1.0.0",
  [string]$SecretsDir = "_secrets"
)
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

Write-Host "== publishing licence server (self-contained single-file win-x64, embedded SQLite) ==" -ForegroundColor Cyan
dotnet publish src/DataVortex.LicenseServer/DataVortex.LicenseServer.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false -p:Version=$Version -o $Output -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "server publish failed" }

# Drop the filled prod config + TLS cert next to the exe so the folder is ready to run as-is.
$prod = Join-Path $SecretsDir "appsettings.Production.json"
$pfx  = Join-Path $SecretsDir "server-tls.pfx"
if ((Test-Path $prod) -and (Test-Path $pfx)) {
  Copy-Item $prod (Join-Path $Output "appsettings.Production.json") -Force
  Copy-Item $pfx  (Join-Path $Output "server-tls.pfx") -Force
  Write-Host "OK -> $Output\ is a ready-to-run bundle (exe + config + cert + dashboard). No database to set up." -ForegroundColor Green
} else {
  Write-Host "OK -> $Output\DataVortex.LicenseServer.exe (no secrets in $SecretsDir, bundle without config/cert)" -ForegroundColor Yellow
}
Write-Host "Deploy: copy $Output to the VPS, run the exe once to read the admin password + TOTP, then install-service.ps1." -ForegroundColor DarkGray
