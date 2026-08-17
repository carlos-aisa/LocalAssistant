[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Model,

    [ValidateRange(1, 20)]
    [int] $Runs = 3,

    [ValidateNotNullOrEmpty()]
    [string] $ApiUrl = "http://localhost:5100",

    [string] $OutputPath
)

$ErrorActionPreference = "Stop"

$cases = @(
    [pscustomobject]@{
        Id = "current-time-es"
        Prompt = "¿Qué hora UTC es ahora?"
        ExpectedTool = $true
    },
    [pscustomobject]@{
        Id = "event-time-es"
        Prompt = "Necesito la hora actual en UTC para registrar este evento."
        ExpectedTool = $true
    },
    [pscustomobject]@{
        Id = "current-time-en"
        Prompt = "What is the current UTC time?"
        ExpectedTool = $true
    },
    [pscustomobject]@{
        Id = "utc-explanation-es"
        Prompt = "Explícame qué significa UTC sin consultar la hora actual."
        ExpectedTool = $false
    },
    [pscustomobject]@{
        Id = "tool-name-literal-es"
        Prompt = "Escribe literalmente el nombre get_current_time y nada más."
        ExpectedTool = $false
    }
)

$baseUri = $ApiUrl.TrimEnd("/")
Invoke-RestMethod -Method Get -Uri "$baseUri/health" | Out-Null

$results = [System.Collections.Generic.List[object]]::new()

foreach ($run in 1..$Runs) {
    foreach ($case in $cases) {
        $body = @{
            message = $case.Prompt
            provider = "ollama"
        } | ConvertTo-Json

        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            $response = Invoke-RestMethod `
                -Method Post `
                -Uri "$baseUri/api/conversations/messages" `
                -ContentType "application/json" `
                -Body $body
            $stopwatch.Stop()

            $tools = @($response.tools)
            $toolNames = @($tools | ForEach-Object { $_.toolName })
            $hasExpectedTool =
                $tools.Count -eq 1 -and
                $tools[0].toolName -eq "get_current_time" -and
                $tools[0].succeeded -eq $true
            $hasNoTools = $tools.Count -eq 0
            $toolDecisionPassed = if ($case.ExpectedTool) {
                $hasExpectedTool -and $response.iterations -ge 2
            }
            else {
                $hasNoTools -and $response.iterations -eq 1
            }
            $passed =
                $null -eq $response.error -and
                -not [string]::IsNullOrWhiteSpace([string] $response.content) -and
                $toolDecisionPassed

            $results.Add([pscustomobject]@{
                run = $run
                caseId = $case.Id
                expectedTool = $case.ExpectedTool
                passed = $passed
                iterations = $response.iterations
                toolNames = $toolNames
                totalMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
                failure = if ($passed) { $null } else { "unexpected_tool_behavior" }
            })
        }
        catch {
            $stopwatch.Stop()
            $results.Add([pscustomobject]@{
                run = $run
                caseId = $case.Id
                expectedTool = $case.ExpectedTool
                passed = $false
                iterations = $null
                toolNames = @()
                totalMilliseconds = [math]::Round($stopwatch.Elapsed.TotalMilliseconds, 1)
                failure = "request_failed"
            })
        }
    }
}

$passedCount = @($results | Where-Object { $_.passed }).Count
$totalCount = $results.Count
$report = [ordered]@{
    schemaVersion = 1
    evaluatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    model = $Model
    apiUrl = $baseUri
    runs = $Runs
    casesPerRun = $cases.Count
    summary = [ordered]@{
        passed = $passedCount
        total = $totalCount
        passRate = [math]::Round($passedCount / $totalCount, 4)
        averageMilliseconds = [math]::Round(
            ($results | Measure-Object -Property totalMilliseconds -Average).Average,
            1)
    }
    results = $results
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $safeModel = $Model -replace "[^a-zA-Z0-9._-]", "-"
    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $OutputPath = Join-Path "artifacts" "tool-calling-$safeModel-$timestamp.json"
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutputPath)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    ($report | ConvertTo-Json -Depth 8),
    [System.Text.UTF8Encoding]::new($false))

$report.summary | Format-List
Write-Host "Report: $resolvedOutputPath"

if ($passedCount -ne $totalCount) {
    exit 1
}
