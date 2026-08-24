# Deploy SD Card Importer to Raspberry Pi
# Usage: .\deploy-to-pi.ps1 [-Host 192.168.1.251] [-User pi] [-Path /home/pi/SdCardCopier] [-Runtime linux-arm64]
# Requires: dotnet CLI, ssh/scp (OpenSSH), and SSH key or password auth to the Pi

param(
    [string]$PiHost = "192.168.1.251",
    [string]$User = "pi",
    [string]$RemotePath = "/home/pi/SdCardCopier",
    [ValidateSet("linux-arm64", "linux-arm")]
    [string]$Runtime = "linux-arm64",
    [switch]$FrameworkDependent
)

# Load .env for PI_* credentials
$EnvFile = Join-Path (Split-Path -Parent $PSScriptRoot) ".env"
if (Test-Path $EnvFile) {
    Get-Content $EnvFile | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]+)=(.*)$') {
            $key = $matches[1].Trim()
            $val = $matches[2].Trim()
            [System.Environment]::SetEnvironmentVariable($key, $val, 'Process')
        }
    }
}

# Override from environment / .env (PI_HOST, PI_USER, PI_PATH, PI_PASS)
if ($env:PI_HOST) { $PiHost = $env:PI_HOST }
if ($env:PI_USER) { $User = $env:PI_USER }
if ($env:PI_PATH) { $RemotePath = $env:PI_PATH }

$ErrorActionPreference = "Stop"
# Prefer PuTTY plink/pscp (Windows) or sshpass (Linux/WSL) when PI_PASS is set
$Plink = $null
$Pscp = $null
if ($env:PI_PASS) {
    $plinkCmd = Get-Command plink -ErrorAction SilentlyContinue; if ($plinkCmd) { $Plink = $plinkCmd.Source }
    if (-not $Plink) { $Plink = Get-ChildItem "${env:ProgramFiles}\PuTTY\plink.exe", "${env:ProgramFiles(x86)}\PuTTY\plink.exe" -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName }
    $Pscp = if ($Plink) { Join-Path (Split-Path $Plink) "pscp.exe" }; if ($Pscp -and -not (Test-Path $Pscp)) { $Pscp = $null }
}
$UsePlink = $Plink -and $Pscp -and $env:PI_PASS
$UseSshPass = -not $UsePlink -and $env:PI_PASS -and (Get-Command sshpass -ErrorAction SilentlyContinue)
function Invoke-Ssh {
    param([string]$Cmd)
    if ($UsePlink) { & $Plink -ssh -batch -pw $env:PI_PASS "${User}@${PiHost}" $Cmd }
    elseif ($UseSshPass) { & sshpass -p $env:PI_PASS ssh "${User}@${PiHost}" $Cmd }
    else { & ssh "${User}@${PiHost}" $Cmd }
}
function Invoke-Scp {
    param([string]$Src, [string]$Dest)
    if ($UsePlink) { & $Pscp -batch -pw $env:PI_PASS $Src $Dest }
    elseif ($UseSshPass) { & sshpass -p $env:PI_PASS scp $Src $Dest }
    else { & scp $Src $Dest }
}
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$PublishSuffix = if ($FrameworkDependent) { "fd" } else { "sc" }
$PublishDir = Join-Path (Join-Path $ProjectRoot "publish") "$Runtime-$PublishSuffix"

Write-Host "Publishing for $Runtime ($(if ($FrameworkDependent) { 'framework-dependent' } else { 'self-contained' }))..." -ForegroundColor Cyan
Push-Location $ProjectRoot
try {
    $selfContained = if ($FrameworkDependent) { "false" } else { "true" }
    dotnet publish -c Release -r $Runtime --self-contained $selfContained -o $PublishDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

$Dest = "${User}@${PiHost}:${RemotePath}"
Write-Host "Copying to $Dest ..." -ForegroundColor Cyan
Invoke-Ssh "mkdir -p $RemotePath"

# Use tar for reliable transfer (avoids Windows scp wildcard issues that can cause "no frameworks found")
$TarFile = Join-Path $env:TEMP "SdCardCopier-deploy.tar.gz"
try {
    tar -czf $TarFile -C $PublishDir .
    if ($LASTEXITCODE -ne 0) { throw "tar failed" }
    $maxRetries = 3
    $scpOk = $false
    for ($r = 1; $r -le $maxRetries; $r++) {
        if ($r -gt 1) { Write-Host "Retry $r of $maxRetries..." -ForegroundColor Yellow; Start-Sleep -Seconds 3 }
        Invoke-Scp $TarFile "${Dest}/"
        if ($LASTEXITCODE -eq 0) { $scpOk = $true; break }
    }
    if (-not $scpOk -and $UsePlink) {
        Write-Host "pscp failed. Trying OpenSSH scp (you may be prompted for password)..." -ForegroundColor Yellow
        & scp $TarFile "${Dest}/"
        if ($LASTEXITCODE -eq 0) { $scpOk = $true }
    }
    if (-not $scpOk) { Write-Host "SCP failed. Check: Pi disk space (ssh pi@$PiHost 'df -h'), network, try Ethernet if on WiFi." -ForegroundColor Red; exit 1 }
    Invoke-Ssh "cd $RemotePath && tar -xzf $(Split-Path -Leaf $TarFile) && rm -f $(Split-Path -Leaf $TarFile)"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    if (Test-Path $TarFile) { Remove-Item $TarFile -Force }
}

# Make SDCardImporter executable (scp/tar does not preserve execute bits)
Invoke-Ssh "chmod +x ${RemotePath}/SDCardImporter 2>/dev/null; true"

# Copy .env (and .env.example if present) so Telegram etc. work on the Pi
$EnvFile = Join-Path $ProjectRoot ".env"
$EnvExample = Join-Path $ProjectRoot ".env.example"
if (Test-Path $EnvFile) {
    Write-Host "Copying .env ..." -ForegroundColor Cyan
    Invoke-Scp $EnvFile "${Dest}/.env"
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    Write-Host ".env not found (optional — create on Pi or add to project and redeploy)" -ForegroundColor Yellow
}
if (Test-Path $EnvExample) {
    Invoke-Scp $EnvExample "${Dest}/.env.example"
}

# Copy run-importer.sh to Pi Desktop
$RunScript = Join-Path $PSScriptRoot "run-importer.sh"
$DesktopPath = "/home/pi/Desktop"
if (Test-Path $RunScript) {
    Write-Host "Copying run-importer.sh to Desktop ..." -ForegroundColor Cyan
    Invoke-Ssh "mkdir -p $DesktopPath"
    Invoke-Scp $RunScript "${User}@${PiHost}:${DesktopPath}/run-importer.sh"
    if ($LASTEXITCODE -eq 0) {
        Invoke-Ssh "chmod +x ${DesktopPath}/run-importer.sh"
    }
}

# Copy pi-cleanup-space.sh to SdCardCopier folder
$CleanupScript = Join-Path $PSScriptRoot "pi-cleanup-space.sh"
if (Test-Path $CleanupScript) {
    Write-Host "Copying pi-cleanup-space.sh ..." -ForegroundColor Cyan
    Invoke-Scp $CleanupScript "${Dest}/pi-cleanup-space.sh"
    if ($LASTEXITCODE -eq 0) {
        Invoke-Ssh "chmod +x ${RemotePath}/pi-cleanup-space.sh"
    }
}

Write-Host "Deploy complete. Binary: $RemotePath/SDCardImporter" -ForegroundColor Green
if ($FrameworkDependent) {
    Write-Host "On the Pi, install .NET 8 runtime (add Microsoft repo first — see README): sudo apt install dotnet-runtime-8.0" -ForegroundColor Yellow
}
Write-Host "On the Pi, run: cd $RemotePath && ./SDCardImporter -w -y --web --delete-skipped --skip-space-check" -ForegroundColor Gray
Write-Host "If 'no frameworks found': run 'uname -m' on the Pi. If armv7l (32-bit), redeploy with: .\deploy-to-pi.ps1 -Runtime linux-arm" -ForegroundColor Yellow
