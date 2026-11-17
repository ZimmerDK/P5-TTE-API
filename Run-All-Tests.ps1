param(
    [switch]$SkipLive,
    [switch]$SkipE2E,
    [switch]$KeepServices
)

$ErrorActionPreference = "Continue"
$servicesStartedByScript = $false
$results = [System.Collections.Generic.List[PSCustomObject]]::new()
$tempDir = Join-Path $PSScriptRoot ".test-results-temp"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

function Run-Suite(
    [string]$Name,
    [string]$Cmd,
    [string[]]$CommandArgs,
    [string]$WorkDir = $PSScriptRoot,
    [string]$XmlFile = $null,
    [string]$XmlType = $null
) {
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    Push-Location $WorkDir
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    & $Cmd @CommandArgs
    $exitCode = $LASTEXITCODE
    $sw.Stop()
    Pop-Location

    $passed = 0; $failed = 0; $skipped = 0; $total = 0

    if ($XmlFile -and (Test-Path $XmlFile)) {
        [xml]$xml = Get-Content $XmlFile
        if ($XmlType -eq "TRX") {
            $c = $xml.getElementsByTagName("Counters")[0]
            if ($c) {
                $passed  = [int]$c.passed
                $failed  = [int]$c.failed
                $skipped = [int]$c.notExecuted
                $total   = [int]$c.total
            }
        } elseif ($XmlType -eq "JUnit") {
            $ts = $xml.getElementsByTagName("testsuite")[0]
            if ($ts) {
                $total   = [int]$ts.tests
                $failed  = [int]$ts.failures + [int]$ts.errors
                $skipped = [int]$ts.skipped
                $passed  = $total - $failed - $skipped
            }
        }
    }

    if ($total -eq 0 -and $exitCode -ne 0) { $failed = 1; $total = 1 }
    $status = if ($exitCode -eq 0 -and $failed -eq 0) { "PASSED" } else { "FAILED" }

    $results.Add([PSCustomObject]@{
        Name        = $Name
        Status      = $status
        Passed      = $passed
        Failed      = $failed
        Skipped     = $skipped
        Total       = $total
        DurationSec = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    })
}

try {
    $apiXml = Join-Path $tempDir "api.trx"
    Run-Suite "API unit and integration tests" "dotnet" @("test", ".\TimeEstimationAPI.Tests\TimeEstimationAPI.Tests.csproj", "--logger", "trx;LogFileName=$apiXml") -XmlFile $apiXml -XmlType "TRX"

    $mlXml = Join-Path $tempDir "ml.xml"
    Run-Suite "ML service unit and integration tests" (Join-Path $PSScriptRoot "ml-service\.venv\Scripts\python.exe") @("-m", "pytest", ".\tests", "-q", "--junitxml=$mlXml") -WorkDir (Join-Path $PSScriptRoot "ml-service") -XmlFile $mlXml -XmlType "JUnit"

    if (-not $SkipLive) {
        $liveXml = Join-Path $tempDir "live.trx"
        Run-Suite "Live provider tests" "dotnet" @("test", ".\TimeEstimationAPI.LiveTests\TimeEstimationAPI.LiveTests.csproj", "--logger", "trx;LogFileName=$liveXml") -XmlFile $liveXml -XmlType "TRX"
    } else {
        $results.Add([PSCustomObject]@{ Name = "Live provider tests"; Status = "SKIPPED"; Passed = 0; Failed = 0; Skipped = 0; Total = 0; DurationSec = 0 })
    }

    if (-not $SkipE2E) {
        Write-Host "`n=== Starting local services for E2E tests ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "Start-LocalServices.ps1")
        if ($LASTEXITCODE -eq 0) {
            $servicesStartedByScript = $true
            $e2eXml = Join-Path $tempDir "e2e.trx"
            Run-Suite "Local black-box E2E tests" "dotnet" @("test", ".\TimeEstimationAPI.E2ETests\TimeEstimationAPI.E2ETests.csproj", "--logger", "trx;LogFileName=$e2eXml") -XmlFile $e2eXml -XmlType "TRX"
        } else {
            $results.Add([PSCustomObject]@{ Name = "Local black-box E2E tests"; Status = "FAILED"; Passed = 0; Failed = 1; Skipped = 0; Total = 1; DurationSec = 0 })
        }
    } else {
        $results.Add([PSCustomObject]@{ Name = "Local black-box E2E tests"; Status = "SKIPPED"; Passed = 0; Failed = 0; Skipped = 0; Total = 0; DurationSec = 0 })
    }

    # Print TEST RUN SUMMARY Table
    Write-Host "`n====================================================================================================" -ForegroundColor Cyan
    Write-Host "                                         TEST RUN SUMMARY                                           " -ForegroundColor Cyan
    Write-Host "====================================================================================================" -ForegroundColor Cyan

    $fmt = "{0,-44} {1,-10} {2,8} {3,8} {4,9} {5,7} {6,10}"
    Write-Host ($fmt -f "Test Suite", "Status", "Passed", "Failed", "Skipped", "Total", "Duration") -ForegroundColor White
    Write-Host ("-" * 100) -ForegroundColor Gray

    $totP = 0; $totF = 0; $totS = 0; $totT = 0; $totD = 0.0; $anyFailed = $false

    foreach ($r in $results) {
        $isSkip = $r.Status -eq "SKIPPED"
        $col = switch ($r.Status) { "PASSED" { [ConsoleColor]::Green } "FAILED" { $anyFailed = $true; [ConsoleColor]::Red } "SKIPPED" { [ConsoleColor]::Yellow } }

        $p = if ($isSkip) { "-" } else { $r.Passed }
        $f = if ($isSkip) { "-" } else { $r.Failed }
        $s = if ($isSkip) { "-" } else { $r.Skipped }
        $t = if ($isSkip) { "-" } else { $r.Total }
        $d = if ($isSkip) { "-" } else { "$($r.DurationSec)s" }

        Write-Host -NoNewline ($r.Name.PadRight(44) + " ")
        Write-Host -NoNewline ($r.Status.PadRight(10) + " ") -ForegroundColor $col
        Write-Host ("{0,8} {1,8} {2,9} {3,7} {4,10}" -f $p, $f, $s, $t, $d)

        if (-not $isSkip) {
            $totP += $r.Passed; $totF += $r.Failed; $totS += $r.Skipped; $totT += $r.Total; $totD += $r.DurationSec
        }
    }

    Write-Host ("-" * 100) -ForegroundColor Gray
    $totStat = if ($anyFailed) { "FAILED" } else { "PASSED" }
    $totCol  = if ($anyFailed) { [ConsoleColor]::Red } else { [ConsoleColor]::Green }
    Write-Host -NoNewline ("TOTALS".PadRight(44) + " ")
    Write-Host -NoNewline ($totStat.PadRight(10) + " ") -ForegroundColor $totCol
    Write-Host ("{0,8} {1,8} {2,9} {3,7} {4,10}" -f $totP, $totF, $totS, $totT, "$([math]::Round($totD, 2))s")
    Write-Host "====================================================================================================" -ForegroundColor Cyan

    if ($anyFailed) { throw "One or more test suites failed." }
} finally {
    if ($servicesStartedByScript -and -not $KeepServices) {
        Write-Host "`n=== Stopping local services ===" -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot "Reload-Services.ps1") -StopOnly
    }
    if (Test-Path $tempDir) {
        $retryCount = 0
        while ($retryCount -lt 5) {
            try {
                Remove-Item -Recurse -Force $tempDir -ErrorAction Stop
                break
            } catch {
                $retryCount++
                Start-Sleep -Milliseconds 200
            }
        }
    }
}
