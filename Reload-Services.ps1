param(
    [switch]$StopOnly,
    [switch]$ApiOnly,
    [switch]$UseOsrm,
    [switch]$UseOverpass,
    [switch]$UseContext,
    [switch]$UseGoogle,
    [switch]$Watch,
    [string]$ModelPath
)

$ErrorActionPreference = "SilentlyContinue"

$stateFile = Join-Path $PSScriptRoot ".service-state.json"

if (Test-Path $stateFile) {
    try {
        $stateJson = Get-Content -Path $stateFile -Raw | ConvertFrom-Json
        $pidsToStop = if ($ApiOnly) {
            if ($stateJson.ApiProcessId) {
                @([int]$stateJson.ApiProcessId)
            } elseif ($stateJson.Pids -and $stateJson.Pids.Count -gt 1) {
                @([int]$stateJson.Pids[1])
            } else {
                @()
            }
        } elseif ($stateJson.Pids) {
            @($stateJson.Pids | ForEach-Object { [int]$_ })
        } else {
            @()
        }
        foreach ($pidToStop in $pidsToStop) {
            Stop-Process -Id $pidToStop -Force -ErrorAction SilentlyContinue
        }
    } catch {
        if ($ApiOnly) {
            throw
        }
    }
}

$ErrorActionPreference = "Stop"

if ($StopOnly) {
    Remove-Item $stateFile -ErrorAction SilentlyContinue
    Write-Host "Stopped all running local services."
    exit 0
}

Write-Host "Reloading services on demand..."

$params = @{}
if ($PSBoundParameters.ContainsKey("ModelPath")) {
    $params["ModelPath"] = $ModelPath
}
foreach ($flagName in @("UseOsrm", "UseOverpass", "UseContext", "UseGoogle", "Watch")) {
    if ($PSBoundParameters.ContainsKey($flagName)) {
        $params[$flagName] = [bool]$PSBoundParameters[$flagName]
    }
}
if (Test-Path $stateFile) {
    $stateJson = Get-Content -Path $stateFile -Raw | ConvertFrom-Json
    if (-not $PSBoundParameters.ContainsKey("ModelPath") -and $stateJson.ModelPath) {
        $params["ModelPath"] = $stateJson.ModelPath
    }

    foreach ($flagName in @("UseOsrm", "UseOverpass", "UseContext", "UseGoogle", "Watch")) {
        if ($PSBoundParameters.ContainsKey($flagName)) {
            continue
        } elseif ($flagName -eq "UseGoogle" -and $stateJson.UseGoogleMaps) {
            $params[$flagName] = $true
        } elseif ($stateJson.$flagName) {
            $params[$flagName] = $true
        }
    }
}

$startScript = Join-Path $PSScriptRoot "Start-LocalServices.ps1"
if ($ApiOnly) {
    $params["ApiOnly"] = $true
}
& $startScript @params
