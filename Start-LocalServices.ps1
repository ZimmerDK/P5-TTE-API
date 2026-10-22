param(
    [string]$ModelPath = "",
    [switch]$UseOsrm
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
    "-m", "uvicorn", "app:app",
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

$pythonProcess = Start-Process `
    -FilePath $pythonExecutable `
    -ArgumentList $pythonArguments `
    -WorkingDirectory $pythonDirectory `
    -PassThru

$apiProcess = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList "run", "--project", $apiProject, "--launch-profile", "http" `
    -WorkingDirectory $PSScriptRoot `
    -PassThru

Write-Host "Python model service started with PID $($pythonProcess.Id) at http://127.0.0.1:8001"
Write-Host "ASP.NET API started with PID $($apiProcess.Id) at http://localhost:5073"
if ($UseOsrm) {
    Write-Host "OSRM/Nominatim routing is enabled."
}
Write-Host "Current model info:"
Write-Host "  http://127.0.0.1:8001/model-info"
Write-Host ""
Write-Host "Health checks:"
Write-Host "  http://127.0.0.1:8001/health"
Write-Host "  http://localhost:5073/health"
Write-Host ""
Write-Host "Stop both processes with:"
Write-Host "  Stop-Process -Id $($pythonProcess.Id),$($apiProcess.Id)"
