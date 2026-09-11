<#
.SYNOPSIS
    Reproducible smoke test for the phase 5 "Windows operational close" increment.

.DESCRIPTION
    Publishes (or reuses) a framework-dependent win-x64 build of the terminal client and
    exercises --version and --diagnostics against it. Asserts that no .wav file appears
    anywhere it shouldn't and that the local DPAPI state file, if present, never exposes a
    credential, bearer, challenge or access token in its readable part. Unless -SkipManual
    is passed, it then guides the operator through the remaining manual steps (pairing, a
    real turn, voice, /stop, /exit) and re-checks for residual processes and audio files
    afterwards.

    This script performs no network egress beyond the loopback base URL it is given, and
    never sends or stores a real credential itself.

.PARAMETER BaseUrl
    Loopback base URL passed to --diagnostics. Defaults to http://localhost:5100; the API
    does not need to be running for the automated checks to pass.

.PARAMETER Provider
    Provider passed to --diagnostics. Defaults to "fake" so the automated checks do not
    depend on Ollama.

.PARAMETER PublishDir
    An existing publish output directory to test instead of publishing a fresh one. Must
    contain LocalAssistant.TerminalClient.exe. When omitted, the script publishes to a
    temporary directory and removes it afterwards.

.PARAMETER SkipManual
    Skip the guided manual section and run only the automated checks.

.EXAMPLE
    .\scripts\Invoke-TerminalClientSmoke.ps1

.EXAMPLE
    .\scripts\Invoke-TerminalClientSmoke.ps1 -PublishDir C:\publish\terminal-client -SkipManual
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $BaseUrl = "http://localhost:5100",

    [ValidateSet("fake", "ollama")]
    [string] $Provider = "fake",

    [string] $PublishDir,

    [switch] $SkipManual
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$csprojPath = Join-Path $repoRoot "src\LocalAssistant.TerminalClient\LocalAssistant.TerminalClient.csproj"
$exeName = "LocalAssistant.TerminalClient.exe"
$sensitiveKeys = @("credential", "accessToken", "bearer", "challenge")
$dpapiStatePath = Join-Path $env:LOCALAPPDATA "LocalAssistant\TerminalClient\private-client.json"

$failures = New-Object System.Collections.Generic.List[string]

function Write-Step {
    param([string] $Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Add-Failure {
    param([string] $Message)
    $script:failures.Add($Message)
    Write-Host "FAIL: $Message" -ForegroundColor Red
}

function Assert-Contains {
    param([string] $Haystack, [string] $Needle, [string] $Description)
    if ($Haystack -notlike "*$Needle*") {
        Add-Failure "$Description (expected to find '$Needle')"
        return $false
    }
    return $true
}

function Get-WavFiles {
    param([string] $Root)
    if (-not (Test-Path -LiteralPath $Root)) {
        return @()
    }

    return Get-ChildItem -LiteralPath $Root -Filter "*.wav" -Recurse -File -ErrorAction SilentlyContinue
}

function Get-ResidualProcesses {
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -in @("LocalAssistant.TerminalClient", "LocalAssistant.Api", "testhost") }
}

# --- Resolve or publish the executable under test -------------------------------------

$publishedByThisRun = $false
if ($PublishDir) {
    Write-Step "Using the existing publish directory: $PublishDir"
    $publishDirectory = $PublishDir
}
else {
    $publishDirectory = Join-Path $env:TEMP ("LocalAssistant.TerminalClient.smoke." + [Guid]::NewGuid().ToString("N"))
    Write-Step "Publishing a framework-dependent win-x64 build to: $publishDirectory"
    & dotnet publish $csprojPath -c Release -r win-x64 --self-contained false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) {
        Add-Failure "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishedByThisRun = $true
}

$exePath = Join-Path $publishDirectory $exeName

try {
    if (-not (Test-Path -LiteralPath $exePath)) {
        Add-Failure "$exeName was not found in $publishDirectory."
    }
    else {
        # --- --version -------------------------------------------------------------

        Write-Step "Checking --version"
        $versionOutput = & $exePath --version
        $versionExitCode = $LASTEXITCODE
        if ($versionExitCode -ne 0) {
            Add-Failure "--version exited with code $versionExitCode, expected 0."
        }

        $versionText = [string]::Join([Environment]::NewLine, $versionOutput)
        if ([string]::IsNullOrWhiteSpace($versionText)) {
            Add-Failure "--version produced no output."
        }
        else {
            Assert-Contains $versionText "LocalAssistant.TerminalClient" "--version did not print the client name" | Out-Null
        }

        # --- --diagnostics -----------------------------------------------------------

        Write-Step "Checking --diagnostics"
        $diagnosticsOutput = & $exePath --diagnostics "--base-url=$BaseUrl" "--provider=$Provider"
        $diagnosticsExitCode = $LASTEXITCODE
        if ($diagnosticsExitCode -ne 0) {
            Add-Failure "--diagnostics exited with code $diagnosticsExitCode, expected 0 (a report is a success even when the API is unreachable)."
        }

        $diagnosticsText = [string]::Join([Environment]::NewLine, $diagnosticsOutput)
        if ([string]::IsNullOrWhiteSpace($diagnosticsText)) {
            Add-Failure "--diagnostics produced no output."
        }
        else {
            Assert-Contains $diagnosticsText "diagnostics" "--diagnostics report is missing its header" | Out-Null
            Assert-Contains $diagnosticsText "Version:" "--diagnostics report is missing the client version" | Out-Null
            Assert-Contains $diagnosticsText "loopback" "--diagnostics report did not classify the base URL as loopback" | Out-Null
            Assert-Contains $diagnosticsText "Local state" "--diagnostics report is missing the local state section" | Out-Null
            Assert-Contains $diagnosticsText "Path:" "--diagnostics report did not name the local state path" | Out-Null

            # The report is expected to say "Bearer: not persisted ..." on purpose, so the
            # sensitive-key check below (exact JSON key names) applies only to the local
            # state file's readable part, not to this narrative text.
        }
    }

    # --- No audio artifacts anywhere they shouldn't be ------------------------------

    Write-Step "Checking for .wav files"
    $wavRoots = @((Get-Location).Path, $publishDirectory) | Select-Object -Unique
    foreach ($root in $wavRoots) {
        $wavFiles = Get-WavFiles -Root $root
        if ($wavFiles.Count -gt 0) {
            Add-Failure "Found $($wavFiles.Count) .wav file(s) under $root; spoken output must never persist audio."
        }
    }

    # --- The local DPAPI state file, if present, never exposes a secret key --------

    Write-Step "Checking the local state file (if any) for sensitive keys"
    if (Test-Path -LiteralPath $dpapiStatePath) {
        $stateText = Get-Content -LiteralPath $dpapiStatePath -Raw
        try {
            $state = $stateText | ConvertFrom-Json

            # Exact key names, case-insensitive: this must not flag protectedCredential or
            # protectedPayload, which are the intentionally encrypted containers (base64
            # ciphertext) and are expected to exist. Only a bare credential/accessToken
            # /bearer/challenge key would mean something leaked in cleartext.
            $propertyNames = $state |
                Get-Member -MemberType NoteProperty |
                Select-Object -ExpandProperty Name
            foreach ($key in $sensitiveKeys) {
                if ($propertyNames | Where-Object { $_ -ieq $key }) {
                    Add-Failure "The local state file's readable part has an unexpected '$key' key."
                }
            }
        }
        catch {
            Add-Failure "The local state file's readable part is not valid JSON: $($_.Exception.Message)"
        }
    }
    else {
        Write-Host "    (no local state file yet at $dpapiStatePath)"
    }

    # --- Guided manual section -------------------------------------------------------

    if (-not $SkipManual) {
        Write-Step "Manual verification"
        Write-Host @"
    With the API stopped, confirm the client shows an actionable "unreachable" state
    (see --diagnostics above) instead of trying to start it.

    With the API started, complete by hand in a real run of $exePath :
      1. Pair or sign in with a private client.
      2. Send one real message and read the response.
      3. Try /voice, /rate, /volume (if a voice is configured).
      4. Trigger /stop during playback and confirm it interrupts cleanly.
      5. Exit with /exit, Ctrl+C and EOF in separate runs; confirm the terminal is restored
         each time and no LocalAssistant.TerminalClient process is left behind.
"@
        Read-Host "Press Enter once the manual steps above are complete"

        Write-Step "Re-checking for residual processes and .wav files"
        $residual = Get-ResidualProcesses
        if ($residual) {
            $names = ($residual | Select-Object -ExpandProperty ProcessName -Unique) -join ", "
            Add-Failure "Residual process(es) still running after the manual steps: $names."
        }

        foreach ($root in $wavRoots) {
            $wavFiles = Get-WavFiles -Root $root
            if ($wavFiles.Count -gt 0) {
                Add-Failure "Found $($wavFiles.Count) .wav file(s) under $root after the manual steps."
            }
        }
    }
    else {
        Write-Host "Skipping the guided manual section (-SkipManual)." -ForegroundColor Yellow
    }
}
finally {
    if ($publishedByThisRun -and (Test-Path -LiteralPath $publishDirectory)) {
        Write-Step "Removing the temporary publish directory"
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# --- Summary ----------------------------------------------------------------------------

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "PASS: all smoke checks succeeded." -ForegroundColor Green
    exit 0
}

Write-Host "FAIL: $($failures.Count) smoke check(s) failed:" -ForegroundColor Red
foreach ($failure in $failures) {
    Write-Host "  - $failure" -ForegroundColor Red
}

exit 1
