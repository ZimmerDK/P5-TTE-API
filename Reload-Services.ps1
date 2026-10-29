param(
    [switch]$StopOnly
)

$ErrorActionPreference = "SilentlyContinue"

$stateFile = Join-Path $PSScriptRoot ".service-state.json"

if (Test-Path $stateFile) {
    try {
        $stateJson = Get-Content -Path $stateFile -Raw | ConvertFrom-Json
        if ($stateJson.Pids) {
            foreach ($pidToStop in $stateJson.Pids) {
                Stop-Process -Id $pidToStop -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        # Ignore read errors
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
& $startScript @params
