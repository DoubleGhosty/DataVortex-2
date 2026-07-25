<#
  Obfuscated single-file publish (Palier D.1 wired into the publish).

  build -> obfuscate DataVortex.Core.dll + DataVortex.Licensing.dll -> publish --no-build so the
  single-file bundle embeds the OBFUSCATED assemblies (string encryption + private/internal renaming;
  KeepPublicApi so the un-obfuscated WPF App can still bind by name).

  IMPORTANT: the single-file bundler pulls project references from each referenced project's OWN build
  output (src/DataVortex.Core/bin/... and src/DataVortex.Licensing/bin/...), NOT from the App's output
  dir -- so we obfuscate the copies in those referenced outputs. Verified: obfuscating them makes the
  Core/Licensing string literals disappear from the produced bundle.

  Usage:  pwsh ./publish.ps1 [-Output dist] [-Version 1.2.3]
#>
param(
  [string]$Output  = "dist",
  [string]$Version = "",
  [string]$HmacKey = "",
  [string]$R2Base  = "https://pub-564be2f53b364ef382926b5afb36fea0.r2.dev"
)
$ErrorActionPreference = "Stop"
# Native (dotnet/obfuscar) exit codes are checked explicitly below; don't let PS 7.4+ auto-throw on
# them (its default differs from 5.1). Cmdlet errors still stop.
$PSNativeCommandUseErrorActionPreference = $false
$proj = "src/DataVortex.App/DataVortex.App.csproj"
$rid  = "win-x64"
$verArgs = @(); if ($Version) { $verArgs = @("-p:Version=$Version") }
# App HMAC key = build-time secret (never committed). Falls back to the generated one in _secrets/ if present.
if (-not $HmacKey -and (Test-Path "_secrets/hmac-key.txt")) { $HmacKey = (Get-Content "_secrets/hmac-key.txt" -Raw).Trim() }
$hmacArgs = @(); if ($HmacKey) { $hmacArgs = @("-p:DvHmacKey=$HmacKey") }
if (-not $HmacKey) { Write-Host "   (no HMAC key -> request signing disabled in this build)" -ForegroundColor DarkYellow }

Write-Host "== 1/3 build ==" -ForegroundColor Cyan
dotnet build $proj -c Release -r $rid -p:SelfContained=true @verArgs @hmacArgs -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "== 2/3 obfuscate (Obfuscar) ==" -ForegroundColor Cyan
$obfExe = Join-Path $env:USERPROFILE ".dotnet\tools\obfuscar.console.exe"
if (-not (Test-Path $obfExe)) {
  dotnet tool install --global Obfuscar.GlobalTool
  if ($LASTEXITCODE -ne 0) { throw "obfuscar install failed" }
}
$targets = 'DataVortex.Core.dll','DataVortex.Licensing.dll'
# Referenced projects' own Release outputs (exclude the App bin -- not what the bundler reads).
$copies = Get-ChildItem src -Recurse -Include $targets -File |
  Where-Object { $_.FullName -match '\\bin\\Release\\' -and $_.FullName -notmatch '\\DataVortex\.App\\' }
if (-not $copies) { throw "no Core/Licensing outputs found to obfuscate" }
foreach ($g in ($copies | Group-Object DirectoryName)) {
  $dir = $g.Name
  $modules = ($g.Group | ForEach-Object { "  <Module file=`"$($_.FullName)`" />" }) -join "`n"
  $cfg = @"
<?xml version="1.0" encoding="utf-8"?>
<Obfuscator>
  <Var name="InPath" value="$dir" />
  <Var name="OutPath" value="$dir\_obf" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="HideStrings" value="true" />
$modules
</Obfuscator>
"@
  $cfgPath = Join-Path $dir "_obfuscar.gen.xml"
  Set-Content $cfgPath $cfg -Encoding UTF8
  Write-Host ("   {0}  ({1} module(s))" -f $dir.Replace((Resolve-Path .).Path + '\',''), $g.Group.Count)
  & $obfExe $cfgPath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "obfuscar failed in $dir" }
  foreach ($m in $g.Group) { Copy-Item (Join-Path "$dir\_obf" $m.Name) $m.FullName -Force }
  Remove-Item "$dir\_obf" -Recurse -Force
  Remove-Item $cfgPath -Force
}

Write-Host "== 3/3 publish single-file (--no-build, embeds the obfuscated assemblies) ==" -ForegroundColor Cyan
dotnet publish $proj -c Release -r $rid --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false `
  @verArgs --no-build -o $Output -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Write-Host "OK -> $Output\DataVortex.exe" -ForegroundColor Green

Write-Host "== 4/4 sign + version + manifest ==" -ForegroundColor Cyan
$exe = Join-Path $Output "DataVortex.exe"
$ver = $Version
if (-not $ver) { $ver = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0] }
# Code-sign with the self-signed cert in _secrets so the anti-tamper self-check is active. Skipped if absent (CI).
if (Test-Path "_secrets/codesign.pfx") {
  $pfxpw = (Get-Content "_secrets/codesign-pfx-password.txt" -Raw).Trim()
  $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]"PersistKeySet,Exportable"
  $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new((Resolve-Path "_secrets/codesign.pfx").Path, $pfxpw, $flags)
  $sig = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -TimestampServer "http://timestamp.digicert.com" -HashAlgorithm SHA256
  $signer = (Get-AuthenticodeSignature $exe).SignerCertificate
  if (-not $signer) { throw "signing failed: $($sig.StatusMessage)" }
  Write-Host "   signed: $($signer.Subject)"
} else {
  Write-Host "   (no _secrets/codesign.pfx -> unsigned)" -ForegroundColor DarkYellow
}
# Version-named copy (immutable CDN URL) + the update manifest. Upload BOTH to R2.
$verExe = Join-Path $Output "DataVortex-$ver.exe"
Copy-Item $exe $verExe -Force
$manifest = [ordered]@{ version = $ver; url = "$R2Base/DataVortex-$ver.exe"; notes = ""; size = (Get-Item $verExe).Length }
$manifest | ConvertTo-Json | Set-Content (Join-Path $Output "latest.json") -Encoding UTF8
Write-Host "   -> $verExe" -ForegroundColor Green
Write-Host "   -> $Output\latest.json  (upload DataVortex-$ver.exe + latest.json to R2)" -ForegroundColor Green
