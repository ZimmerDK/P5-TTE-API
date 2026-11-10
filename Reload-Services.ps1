param(
    [switch]$StopOnly,
    [switch]$ApiOnly
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
if (Test-Path $stateFile) {
    $stateJson = Get-Content -Path $stateFile -Raw | ConvertFrom-Json
    if ($stateJson.ModelPath) { $params["ModelPath"] = $stateJson.ModelPath }
    if ($stateJson.UseOsrm) { $params["UseOsrm"] = $true }
    if ($stateJson.UseOverpass) { $params["UseOverpass"] = $true }
    if ($stateJson.UseContext) { $params["UseContext"] = $true }
    if ($stateJson.Watch) { $params["Watch"] = $true }
}

$startScript = Join-Path $PSScriptRoot "Start-LocalServices.ps1"
if ($ApiOnly) {
    $params["ApiOnly"] = $true
}
& $startScript @params
