param(
    [string]$ModelPath = "",
    [switch]$UseOsrm,
    [switch]$UseOverpass,
    [switch]$UseContext,
    [switch]$Watch
)

$ErrorActionPreference = "Stop"

$pythonDirectory = Join-Path $PSScriptRoot "ml-service"
$pythonExecutable = Join-Path $pythonDirectory ".venv\Scripts\python.exe"
$apiProject = Join-Path $PSScriptRoot "TimeEstimationAPI\TimeEstimationAPI.csproj"
$envFile = Join-Path $pythonDirectory ".env"

if (-not (Test-Path $pythonExecutable)) {
    throw "Python virtual environment not found. Run: .\ml-service\.venv\Scripts\python.exe -m pip install -r .\ml-service\requirements.txt"
}

$modelFileNames = @{
    DecisionTreeRegressor = "DecisionTreeRegressor.joblib"
    LinearRegression = "LinearRegression.joblib"
    RandomForestRegressor = "RandomForestRegressor.joblib"
    Ridge = "Ridge.joblib"
    XGBRegressor = "XGBRegressor.joblib"
}

if (Test-Path $envFile) {
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$' -and
            -not $line.TrimStart().StartsWith("#")) {
            $name = $Matches[1]
            $value = $Matches[2].Trim().Trim('"').Trim("'")
            [Environment]::SetEnvironmentVariable($name, $value, "Process")
        }
    }
} else {
    throw "Missing ml-service\.env. Create it with: Copy-Item .\ml-service\.env.example .\ml-service\.env"
}

$configuredModelPath = if ($ModelPath) { $ModelPath } elseif ($env:MODEL_PATH) {
    $env:MODEL_PATH
} else {
    $modelName = if ($env:MODEL_NAME) { $env:MODEL_NAME } else { "XGBRegressor" }
    $modelDirectory = if ($env:MODEL_DIR) { $env:MODEL_DIR } else { "models" }
    if (-not $modelFileNames.ContainsKey($modelName)) {
        throw "Unsupported MODEL_NAME '$modelName'. Check ml-service\.env.example."
    }
    Join-Path $modelDirectory $modelFileNames[$modelName]
}

$resolvedModelPath = if ([System.IO.Path]::IsPathRooted($configuredModelPath)) {
    $configuredModelPath
} else {
    Join-Path $pythonDirectory $configuredModelPath
}

if (-not (Test-Path $resolvedModelPath)) {
    throw "Joblib model not found at '$resolvedModelPath'. Check MODEL_NAME/MODEL_PATH in ml-service\.env."
}

$pythonArguments = @(
    "-m", "uvicorn", "app:app"
)
if ($Watch) {
    $pythonArguments += "--reload"
}
$pythonArguments += @(
    "--host", "127.0.0.1",
    "--port", "8001"
)

$env:MODEL_PATH = $resolvedModelPath
$env:MODEL_INPUT_MODE = if ($env:MODEL_INPUT_MODE) { $env:MODEL_INPUT_MODE } else { "segments" }
$env:Model__UsePlaceholder = "false"
$env:Model__UsePythonService = "true"
$env:Model__PythonServiceUrl = "http://127.0.0.1:8001"
if ($UseOsrm) {
    $env:Osm__UseOsrm = "true"
}
if ($UseOverpass) {
    $env:Osm__UseOverpass = "true"
} else {
    $env:Osm__UseOverpass = "false"
}
if ($UseContext) {
    $env:Context__Enabled = "true"
} else {
    $env:Context__Enabled = "false"
}

$pythonProcess = Start-Process `
    -FilePath $pythonExecutable `
    -ArgumentList $pythonArguments `
    -WorkingDirectory $pythonDirectory `
    -PassThru

$apiArguments = if ($Watch) {
    @("watch", "run", "--project", $apiProject, "--launch-profile", "http")
} else {
    @("run", "--project", $apiProject, "--launch-profile", "http")
}

$apiProcess = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList $apiArguments `
    -WorkingDirectory $PSScriptRoot `
    -PassThru

$stateFile = Join-Path $PSScriptRoot ".service-state.json"
$stateData = @{
    Pids = @($pythonProcess.Id, $apiProcess.Id)
    ModelPath = $ModelPath
    UseOsrm = [bool]$UseOsrm
    UseOverpass = [bool]$UseOverpass
    UseContext = [bool]$UseContext
    Watch = [bool]$Watch
}
$stateData | ConvertTo-Json | Set-Content -Path $stateFile -Force

Write-Host "Python model service started with PID $($pythonProcess.Id) at http://127.0.0.1:8001"
Write-Host "ASP.NET API started with PID $($apiProcess.Id) at http://localhost:5073"
if ($Watch) {
    Write-Host "Auto-reload on save is ENABLED."
} else {
    Write-Host "Auto-reload on save is DISABLED (default)."
    Write-Host "To hot-reload services on demand when ready, run: .\Reload-Services.ps1"
}
if ($UseOsrm) {
    Write-Host "OSRM/Nominatim routing is enabled."
}
if ($UseOverpass) {
    Write-Host "Overpass OSM tag enrichment is enabled."
} else {
    Write-Host "Overpass OSM tag enrichment is disabled (using fallback tags)."
}
if ($UseContext) {
    Write-Host "Spatial context density enrichment is enabled (Context__Enabled = true)."
    Write-Host "Note: Spatial context service must be started manually if needed (e.g., via Docker):"
    Write-Host "  cd C:\Dev\spatial-context-service.v2; docker compose up -d --build"
} else {
    Write-Host "Spatial context density enrichment is disabled."
}
Write-Host ""
Write-Host "Health checks:"
Write-Host "  http://127.0.0.1:8001/health"
if ($UseContext) {
    Write-Host "  http://localhost:8010/health"
}
Write-Host "  http://localhost:5073/health"
Write-Host ""
Write-Host "Reload services on command:"
Write-Host "  .\Reload-Services.ps1"
Write-Host ""
Write-Host "Stop local background processes:"
Write-Host "  .\Reload-Services.ps1 -StopOnly  (or Stop-Process -Id $($pythonProcess.Id),$($apiProcess.Id))"
