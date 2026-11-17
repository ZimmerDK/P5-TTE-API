param(
    [switch]$SkipLive,
    [switch]$SkipE2E,
    [switch]$KeepServices
)

$ErrorActionPreference = "Stop"
$servicesStartedByScript = $false

function Invoke-TestCommand(
    [string]$Description,
    [string]$FilePath,
    [string[]]$Arguments,
    [string]$WorkingDirectory = $PSScriptRoot
) {
    Write-Host "`n=== $Description ===" -ForegroundColor Cyan
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
}

try {
    Invoke-TestCommand `
        "API unit and integration tests" `
        "dotnet" `
        @("test", ".\TimeEstimationAPI.Tests\TimeEstimationAPI.Tests.csproj")

    Invoke-TestCommand `
        "ML service unit and integration tests" `
        (Join-Path $PSScriptRoot "ml-service\.venv\Scripts\python.exe") `
        @("-m", "pytest", ".\tests", "-q") `
        (Join-Path $PSScriptRoot "ml-service")

    if (-not $SkipLive) {
        Invoke-TestCommand `
            "Live provider tests" `
            "dotnet" `
            @("test", ".\TimeEstimationAPI.LiveTests\TimeEstimationAPI.LiveTests.csproj")
    } else {
        Write-Host "`n=== Live provider tests skipped ===" -ForegroundColor Yellow
    }

    if (-not $SkipE2E) {
        Write-Host "`n=== Starting local services for E2E tests ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "Start-LocalServices.ps1")
        if ($LASTEXITCODE -ne 0) {
            throw "Local services failed to start with exit code $LASTEXITCODE."
        }
        $servicesStartedByScript = $true

        Invoke-TestCommand `
            "Local black-box E2E tests" `
            "dotnet" `
            @("test", ".\TimeEstimationAPI.E2ETests\TimeEstimationAPI.E2ETests.csproj")
    } else {
        Write-Host "`n=== Local black-box E2E tests skipped ===" -ForegroundColor Yellow
    }

    Write-Host "`nAll requested test suites passed." -ForegroundColor Green
} finally {
    if ($servicesStartedByScript -and -not $KeepServices) {
        Write-Host "`n=== Stopping local services ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "Reload-Services.ps1") -StopOnly
    }
}
