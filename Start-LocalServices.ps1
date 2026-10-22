param(
    [string]$ModelPath = "$PSScriptRoot\ml-service\models\XGBRegressor.joblib",
    [switch]$UseOsrm
)

$ErrorActionPreference = "Stop"

$pythonDirectory = Join-Path $PSScriptRoot "ml-service"
$pythonExecutable = Join-Path $pythonDirectory ".venv\Scripts\python.exe"
$apiProject = Join-Path $PSScriptRoot "TimeEstimationAPI\TimeEstimationAPI.csproj"

if (-not (Test-Path $pythonExecutable)) {
    throw "Python virtual environment not found. Run: .\ml-service\.venv\Scripts\python.exe -m pip install -r .\ml-service\requirements.txt"
}

if (-not (Test-Path $ModelPath)) {
    throw "Joblib model not found at '$ModelPath'. Supply -ModelPath or place the selected model in ml-service\models."
}

$pythonArguments = @(
    "-m", "uvicorn", "app:app",
    "--host", "127.0.0.1",
    "--port", "8001"
)

$env:MODEL_PATH = $ModelPath
$env:MODEL_INPUT_MODE = "segments"
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
