[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PythonPath
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repositoryRoot 'tests\LocalAssistant.Tests\LocalAssistant.Tests.csproj'
$previousPythonPath = $env:LOCALASSISTANT_KOKORO_PYTHON_PATH
$previousServiceRoot = $env:LOCALASSISTANT_KOKORO_SERVICE_ROOT
$previousRequirement = $env:LOCALASSISTANT_KOKORO_REQUIRE_INTEROP

try {
    $env:LOCALASSISTANT_KOKORO_PYTHON_PATH = $PythonPath
    $env:LOCALASSISTANT_KOKORO_SERVICE_ROOT = Join-Path $repositoryRoot 'services\kokoro-tts'
    $env:LOCALASSISTANT_KOKORO_REQUIRE_INTEROP = '1'

    dotnet test $testProject -c Release --nologo --filter 'FullyQualifiedName~KokoroDpapiInteropTests'
    if ($LASTEXITCODE -ne 0) {
        throw 'Kokoro DPAPI interoperability tests failed.'
    }
}
finally {
    $env:LOCALASSISTANT_KOKORO_PYTHON_PATH = $previousPythonPath
    $env:LOCALASSISTANT_KOKORO_SERVICE_ROOT = $previousServiceRoot
    $env:LOCALASSISTANT_KOKORO_REQUIRE_INTEROP = $previousRequirement
}
