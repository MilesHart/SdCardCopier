param(
    [ValidateSet("build", "publish")]
    [string]$Action = "build",

    [ValidateSet("importer", "desktop", "solution")]
    [string]$Project = "importer",

    [ValidateSet("win-x64", "linux-x64", "linux-arm", "linux-arm64")]
    [string]$Runtime = "win-x64",

    [string]$Configuration = "Release",

    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot

switch ($Project) {
    "importer" { $target = Join-Path $projectRoot "SDCardImporter.csproj" }
    "desktop"  { $target = Join-Path $projectRoot "SDCardImporter.Desktop\SDCardImporter.Desktop.csproj" }
    "solution" { $target = Join-Path $projectRoot "SDCardImporter.sln" }
    default     { throw "Unsupported project: $Project" }
}

if (-not (Test-Path $target)) {
    throw "Target not found: $target"
}

Push-Location $projectRoot
try {
    if ($Action -eq "build") {
        Write-Host "Running: dotnet build $target -c $Configuration" -ForegroundColor Cyan
        dotnet build $target -c $Configuration
        exit $LASTEXITCODE
    }

    if ($Project -eq "solution") {
        throw "Publish does not support Project=solution. Use importer or desktop."
    }

    $publishSuffix = if ($SelfContained) { "sc" } else { "fd" }
    $outputDir = Join-Path (Join-Path $projectRoot "publish") "$Runtime-$publishSuffix"
    $selfContainedValue = if ($SelfContained) { "true" } else { "false" }

    Write-Host "Running: dotnet publish $target -c $Configuration -r $Runtime --self-contained $selfContainedValue -o $outputDir" -ForegroundColor Cyan
    dotnet publish $target -c $Configuration -r $Runtime --self-contained $selfContainedValue -o $outputDir
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
