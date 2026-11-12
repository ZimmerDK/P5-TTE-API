param(
    [string]$ModelPath = "",
    [switch]$UseOsrm,
    [switch]$UseOverpass,
    [switch]$UseContext,
    [switch]$UseGoogle,
    [switch]$Watch,
    [switch]$ApiOnly
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
    XGBRegressor = "XGBRegressor.json"
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
$env:Model__PythonServiceUrl = "http://127.0.0.1:8001"
if ($UseOsrm) {
    $env:Osm__UseOsrm = "true"
} else {
    $env:Osm__UseOsrm = "false"
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
if ($UseGoogle) {
    $env:GoogleMaps__Enabled = "true"
} else {
    $env:GoogleMaps__Enabled = "false"
}
$stateFile = Join-Path $PSScriptRoot ".service-state.json"
$pythonProcess = $null
if ($ApiOnly) {
    if (-not (Test-Path $stateFile)) {
        throw "Cannot reload only the API because no service state was found. Start both services first."
    }

    $stateJson = Get-Content -Path $stateFile -Raw | ConvertFrom-Json
    $pythonPid = if ($stateJson.ModelProcessId) {
        [int]$stateJson.ModelProcessId
    } elseif ($stateJson.Pids) {
        [int]$stateJson.Pids[0]
    } else {
        0
    }
    if ($pythonPid -le 0) {
        throw "Cannot reload only the API because the ML service process is unknown."
    }

    $pythonProcess = Get-Process -Id $pythonPid -ErrorAction SilentlyContinue
    if (-not $pythonProcess) {
        throw "Cannot reload only the API because the ML service is not running."
    }
} else {
    $pythonProcess = Start-Process `
        -FilePath $pythonExecutable `
        -ArgumentList $pythonArguments `
        -WorkingDirectory $pythonDirectory `
        -PassThru
}

# Do not start or report the API as ready until the model service is ready.
$pythonReady = $false
for ($attempt = 1; $attempt -le 30; $attempt++) {
    if ($pythonProcess.HasExited) {
        throw "Python model service exited during startup with code $($pythonProcess.ExitCode)."
    }

    try {
        $healthResponse = Invoke-WebRequest `
            -Uri "http://127.0.0.1:8001/health" `
            -UseBasicParsing `
            -TimeoutSec 2
        if ($healthResponse.StatusCode -eq 200) {
            $pythonReady = $true
            break
        }
    } catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $pythonReady) {
    Stop-Process -Id $pythonProcess.Id -Force -ErrorAction SilentlyContinue
    throw "Python model service did not become ready at http://127.0.0.1:8001 within 30 seconds."
}

$apiArguments = if ($Watch) {
    @("watch", "run", "--no-restore", "--project", $apiProject, "--launch-profile", "http")
} else {
    @("run", "--no-restore", "--project", $apiProject, "--launch-profile", "http")
}

$apiProcess = Start-Process `
    -FilePath "dotnet" `
    -ArgumentList $apiArguments `
    -WorkingDirectory $PSScriptRoot `
    -PassThru

$apiReady = $false
for ($attempt = 1; $attempt -le 30; $attempt++) {
    if ($apiProcess.HasExited) {
        if (-not $ApiOnly) {
            Stop-Process -Id $pythonProcess.Id -Force -ErrorAction SilentlyContinue
        }
        throw "ASP.NET API exited during startup with code $($apiProcess.ExitCode)."
    }

    try {
        $healthResponse = Invoke-WebRequest `
            -Uri "http://localhost:5073/health" `
            -UseBasicParsing `
            -TimeoutSec 2
        if ($healthResponse.StatusCode -eq 200) {
            $apiReady = $true
            break
        }
    } catch {
        Start-Sleep -Seconds 1
    }
}

if (-not $apiReady) {
    Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    if (-not $ApiOnly) {
        Stop-Process -Id $pythonProcess.Id -Force -ErrorAction SilentlyContinue
    }
    throw "ASP.NET API did not become ready at http://localhost:5073 within 30 seconds."
}

$stateData = @{
    Pids = @($pythonProcess.Id, $apiProcess.Id)
    ModelProcessId = $pythonProcess.Id
    ApiProcessId = $apiProcess.Id
    ModelPath = $ModelPath
    UseOsrm = [bool]$UseOsrm
    UseOverpass = [bool]$UseOverpass
    UseContext = [bool]$UseContext
    UseGoogle = [bool]$UseGoogle
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
Write-Host ""
Write-Host "Model file in use: $resolvedModelPath"
Write-Host ""
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
} else {
    Write-Host "Spatial context density enrichment is disabled."
}
if ($UseGoogle) {
    Write-Host "Google Routes comparison is enabled."
} else {
    Write-Host "Google Routes comparison is disabled."
}
Write-Host ""
Write-Host "Health checks:"
Write-Host "  http://127.0.0.1:8001/health (Python model service)  OR  http://localhost:5073/health (ASP.NET API)"
if ($UseContext) {
    Write-Host "  http://localhost:8010/health (Context service)"
}
Write-Host ""
Write-Host "Reload services on command (supports -UseXXX:$true and -UseXXX:$false):"
Write-Host "  .\Reload-Services.ps1  OR  .\Reload-Services.ps1 -ApiOnly  (restart only the ASP.NET API)"
Write-Host "Stop local background processes:"
Write-Host "  .\Reload-Services.ps1 -StopOnly  (or Stop-Process -Id $($pythonProcess.Id),$($apiProcess.Id))"
