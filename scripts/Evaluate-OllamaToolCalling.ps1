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
        ExpectedTool = "get_current_time"
        ExpectedAuthoritativeTime = $true
    },
    [pscustomobject]@{
        Id = "event-time-es"
        Prompt = "Necesito la hora actual en UTC para registrar este evento."
        ExpectedTool = "get_current_time"
        ExpectedAuthoritativeTime = $true
    },
    [pscustomobject]@{
        Id = "current-time-en"
        Prompt = "What is the current UTC time?"
        ExpectedTool = "get_current_time"
        ExpectedAuthoritativeTime = $true
    },
    [pscustomobject]@{
        Id = "temperature-celsius-to-fahrenheit-es"
        Prompt = "Convierte 100 grados Celsius a Fahrenheit."
        ExpectedTool = "convert_temperature"
        ExpectedAuthoritativeTime = $false
    },
    [pscustomobject]@{
        Id = "temperature-fahrenheit-to-celsius-en"
        Prompt = "Convert 32 degrees Fahrenheit to Celsius."
        ExpectedTool = "convert_temperature"
        ExpectedAuthoritativeTime = $false
    },
    [pscustomobject]@{
        Id = "utc-explanation-es"
        Prompt = "Explícame qué significa UTC sin consultar la hora actual."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
    },
    [pscustomobject]@{
        Id = "temperature-explanation-es"
        Prompt = "Explícame cómo se convierten Celsius y Fahrenheit sin hacer una conversión."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
    },
    [pscustomobject]@{
        Id = "tool-name-literal-es"
        Prompt = "Escribe literalmente el nombre get_current_time y nada más."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
        ExpectedConfirmationTool = $null
    },
    [pscustomobject]@{
        Id = "user-preferred-name-es"
        Prompt = "Me llamo Usuario de prueba."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
        ExpectedConfirmationTool = "set_user_preferred_name"
    },
    [pscustomobject]@{
        Id = "assistant-name-distinction-es"
        Prompt = "Mi nombre es Usuario de prueba, no cambies el tuyo."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
        ExpectedConfirmationTool = "set_user_preferred_name"
    },
    [pscustomobject]@{
        Id = "temporary-location-es"
        Prompt = "Estamos en Ciudad de prueba."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
        ExpectedConfirmationTool = $null
    },
    [pscustomobject]@{
        Id = "household-location-es"
        Prompt = "Quiero guardar que mi hogar está en Ciudad de prueba y usa Europe/Madrid."
        ExpectedTool = $null
        ExpectedAuthoritativeTime = $false
        ExpectedConfirmationTool = "set_household_location"
    }
)

$baseUri = $ApiUrl.TrimEnd("/")
Invoke-RestMethod -Method Get -Uri "$baseUri/health" | Out-Null
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($env:LOCALASSISTANT_API_KEY)) {
    $headers["X-LocalAssistant-Api-Key"] = $env:LOCALASSISTANT_API_KEY
}

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
                -Headers $headers `
                -Body $body
            $stopwatch.Stop()

            $tools = @($response.tools)
            $toolNames = @($tools | ForEach-Object { $_.toolName })
            $hasAuthoritativeTime = @(
                $tools | Where-Object {
                    $_.toolCallId -eq "authoritative-current-time" -and
                    $_.toolName -eq "get_current_time" -and
                    $_.succeeded -eq $true
                }).Count -eq 1
            $hasExpectedTool =
                $tools.Count -eq 1 -and
                $tools[0].toolName -eq $case.ExpectedTool -and
                $tools[0].succeeded -eq $true
            $hasExpectedConfirmation =
                $null -ne $case.ExpectedConfirmationTool -and
                $null -ne $response.confirmation -and
                $response.confirmation.toolName -eq $case.ExpectedConfirmationTool
            $toolDecisionPassed = if ($case.ExpectedAuthoritativeTime) {
                $hasAuthoritativeTime
            }
            elseif ($null -ne $case.ExpectedConfirmationTool) {
                $hasExpectedConfirmation
            }
            elseif ($null -ne $case.ExpectedTool) {
                $hasExpectedTool -and $response.iterations -ge 2
            }
            else {
                $tools.Count -eq 0 -and $response.iterations -eq 1
            }
            $passed =
                $null -eq $response.error -and
                ($hasExpectedConfirmation -or
                    -not [string]::IsNullOrWhiteSpace([string] $response.content)) -and
                $toolDecisionPassed

            $results.Add([pscustomobject]@{
                run = $run
                caseId = $case.Id
                expectedTool = $case.ExpectedTool
                expectedAuthoritativeTime = $case.ExpectedAuthoritativeTime
                expectedConfirmationTool = $case.ExpectedConfirmationTool
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
    schemaVersion = 2
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
