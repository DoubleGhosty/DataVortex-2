#Requires -RunAsAdministrator
<#
  RUN THIS ON THE VPS, as Administrator.

  Installs the licence server as an auto-start Windows Service running in Production, with auto-restart on crash,
  and opens ONLY inbound TCP 443 (the client API). The admin surface stays loopback-only and is never exposed.

  Expects, in -InstallDir:
    DataVortex.LicenseServer.exe   (+ appsettings.json + wwwroot\)  from publish-server.ps1
    appsettings.Production.json                                     from _secrets\ (fill DB + admin passwords)
    server-tls.pfx                                                  from _secrets\

  Usage (elevated PowerShell):  .\install-service.ps1 -InstallDir C:\DataVortex
#>
param(
  [string]$InstallDir  = "C:\DataVortex",
  [string]$ServiceName = "DataVortexLicense",
  [string]$DisplayName = "DataVortex License Server"
)
$ErrorActionPreference = "Stop"

$exe = Join-Path $InstallDir "DataVortex.LicenseServer.exe"
foreach ($f in @($exe,
                 (Join-Path $InstallDir "appsettings.Production.json"),
                 (Join-Path $InstallDir "server-tls.pfx"))) {
  if (-not (Test-Path $f)) { throw "Missing required file: $f" }
}

# (Re)create the service.
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
  Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
  sc.exe delete $ServiceName | Out-Null
  Start-Sleep -Seconds 2
}
New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName $DisplayName -StartupType Automatic | Out-Null

# Service process environment: Production => loads appsettings.Production.json (DB, admin, HMAC, signing key, TLS).
Set-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment `
  -Value @("ASPNETCORE_ENVIRONMENT=Production") -Type MultiString

# Auto-restart on crash (5s, 5s, then every 10s; reset the counter daily).
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/10000 | Out-Null

# Firewall: allow inbound 443 only. The admin (loopback) is NEVER opened to the network.
if (-not (Get-NetFirewallRule -DisplayName "DataVortex License 443" -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName "DataVortex License 443" -Direction Inbound -Protocol TCP -LocalPort 443 `
    -Action Allow -Profile Any | Out-Null
}

Start-Service $ServiceName
Start-Sleep -Seconds 3
Get-Service $ServiceName | Format-List Name, Status, StartType
Write-Host "Health check (run on the VPS):  curl.exe -k https://localhost/api/v1/ping" -ForegroundColor Cyan
Write-Host "Admin dashboard (on the VPS, via RDP):  https://localhost/" -ForegroundColor Cyan
