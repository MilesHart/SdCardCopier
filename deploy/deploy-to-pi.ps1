# Deploy SD Card Importer to Raspberry Pi
# Usage: .\deploy-to-pi.ps1 [-Host 192.168.1.251] [-User pi] [-Path /home/pi/SdCardCopier] [-Runtime linux-arm64]
# Requires: dotnet CLI, ssh/scp (OpenSSH), and SSH key or password auth to the Pi

param(
    [string]$PiHost = "192.168.1.251",
    [string]$User = "pi",
    [string]$RemotePath = "/home/pi/SdCardCopier",
    [ValidateSet("linux-arm64", "linux-arm")]
    [string]$Runtime = "linux-arm64"
)

# Override from environment if set (works in PowerShell 5.1 and 7)
if ($env:PI_HOST) { $PiHost = $env:PI_HOST }
if ($env:PI_USER) { $User = $env:PI_USER }
if ($env:PI_PATH) { $RemotePath = $env:PI_PATH }

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$PublishDir = Join-Path (Join-Path $ProjectRoot "publish") $Runtime

Write-Host "Publishing for $Runtime..." -ForegroundColor Cyan
Push-Location $ProjectRoot
try {
    dotnet publish -c Release -r $Runtime --self-contained -o $PublishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

$Dest = "${User}@${PiHost}:${RemotePath}"
Write-Host "Copying to $Dest ..." -ForegroundColor Cyan
& ssh "${User}@${PiHost}" "mkdir -p $RemotePath"
& scp -r "$PublishDir\*" $Dest
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Copy .env (and .env.example if present) so Telegram etc. work on the Pi
$EnvFile = Join-Path $ProjectRoot ".env"
$EnvExample = Join-Path $ProjectRoot ".env.example"
if (Test-Path $EnvFile) {
    Write-Host "Copying .env ..." -ForegroundColor Cyan
    & scp $EnvFile "${Dest}/.env"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    Write-Host ".env not found (optional — create on Pi or add to project and redeploy)" -ForegroundColor Yellow
}
if (Test-Path $EnvExample) {
    & scp $EnvExample "${Dest}/.env.example"
}

Write-Host "Deploy complete. Binary: $RemotePath/SDCardImporter" -ForegroundColor Green
Write-Host "On the Pi, run: $RemotePath/SDCardImporter -w -y -d /mnt/footage  (or your destination)" -ForegroundColor Gray
