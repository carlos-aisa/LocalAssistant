[CmdletBinding()]
param(
    [ValidateSet("ollama", "fake")]
    [string] $Provider = "ollama",

    [ValidateNotNullOrEmpty()]
    [string] $BaseUrl = "http://localhost:5100",

    [string] $Scenario = "direct",

    [switch] $PromptForApiKey,

    [switch] $VerboseOutput
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http

$supportedFakeScenarios = @("direct", "time", "temperature")
$conversationId = $null
$apiKey = $null

function Get-ApiKey {
    if ($PromptForApiKey) {
        $secureApiKey = Read-Host "Local API key" -AsSecureString
        $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureApiKey)

        try {
            return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        }
        finally {
            [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
        }
    }

    return $env:LOCALASSISTANT_API_KEY
}

function ConvertTo-ApiPath {
    param([Parameter(Mandatory = $true)][string] $Path)

    return "$baseUri$Path"
}

function Invoke-ApiRequest {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("GET", "POST")]
        [string] $Method,

        [Parameter(Mandatory = $true)]
        [string] $Path,

        [object] $Body
    )

    $request = [System.Net.Http.HttpRequestMessage]::new(
        [System.Net.Http.HttpMethod]::new($Method),
        (ConvertTo-ApiPath $Path))

    try {
        if (-not [string]::IsNullOrWhiteSpace($apiKey)) {
            $request.Headers.Add("X-LocalAssistant-Api-Key", $apiKey)
        }

        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 8 -Compress
            $request.Content = [System.Net.Http.StringContent]::new(
                $json,
                [System.Text.Encoding]::UTF8,
                "application/json")
        }

        try {
            $response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
        }
        catch {
            return [pscustomobject]@{
                ConnectionError = $_.Exception.Message
                StatusCode = $null
                Body = $null
            }
        }

        try {
            $responseText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $responseBody = $null
            if (-not [string]::IsNullOrWhiteSpace($responseText)) {
                try {
                    $responseBody = $responseText | ConvertFrom-Json
                }
                catch {
                    $responseBody = $null
                }
            }

            return [pscustomobject]@{
                ConnectionError = $null
                StatusCode = [int] $response.StatusCode
                Body = $responseBody
            }
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

function Get-ErrorCategory {
    param([Nullable[int]] $StatusCode)

    switch ($StatusCode) {
        401 { return "Authentication error" }
        400 { return "Validation error" }
        403 { return "Authorization error" }
        502 { return "Provider error" }
        504 { return "Provider timeout" }
        default { return "API error" }
    }
}

function Show-ApiError {
    param([Parameter(Mandatory = $true)] $Response)

    if ($null -ne $Response.ConnectionError) {
        Write-Host "Connection error: Unable to reach the LocalAssistant API. $($Response.ConnectionError)" -ForegroundColor Red
        return
    }

    $message = $null
    if ($null -ne $Response.Body) {
        if ($null -ne $Response.Body.error -and
            -not [string]::IsNullOrWhiteSpace([string] $Response.Body.error.message)) {
            $message = [string] $Response.Body.error.message
        }
        elseif (-not [string]::IsNullOrWhiteSpace([string] $Response.Body.title)) {
            $message = [string] $Response.Body.title
        }
        elseif ($null -ne $Response.Body.errors) {
            $message = "The API rejected one or more request fields."
        }
    }

    if ([string]::IsNullOrWhiteSpace($message)) {
        $message = "The API returned HTTP $($Response.StatusCode)."
    }

    Write-Host "$(Get-ErrorCategory $Response.StatusCode): $message" -ForegroundColor Red
}

function Show-ConversationResponse {
    param([Parameter(Mandatory = $true)] $Response)

    $body = $Response.Body
    if ($null -eq $body) {
        Show-ApiError $Response
        return $false
    }

    if ($null -ne $body.conversationId) {
        $script:conversationId = [guid] $body.conversationId
    }

    if (-not [string]::IsNullOrWhiteSpace([string] $body.content)) {
        Write-Host "`nJarvis: $($body.content)" -ForegroundColor Cyan
    }

    if ($null -ne $body.error) {
        Write-Host "Error [$($body.error.code)]: $($body.error.message)" -ForegroundColor Red
    }

    $toolNames = @($body.tools | ForEach-Object { $_.toolName })
    $toolsSummary = if ($toolNames.Count -eq 0) { "none" } else { $toolNames -join ", " }
    Write-Host "Conversation: $($script:conversationId) | Iterations: $($body.iterations) | Tools: $toolsSummary"

    if ($VerboseOutput -and $null -ne $body.timings) {
        Write-Host (
            "Timings: total {0} ms, provider {1} ms, tools {2} ms" -f
            $body.timings.totalMilliseconds,
            $body.timings.providerMilliseconds,
            $body.timings.toolsMilliseconds)
    }

    return $null -ne $body.confirmation
}

function Resolve-ToolConfirmation {
    param([Parameter(Mandatory = $true)] $Confirmation)

    Write-Host (
        "Confirmation required for tool '{0}' before {1}." -f
        $Confirmation.toolName,
        $Confirmation.expiresAtUtc) -ForegroundColor Yellow

    while ($true) {
        $decision = (Read-Host "Type approve or reject").Trim().ToLowerInvariant()
        if ($decision -eq "approve" -or $decision -eq "reject") {
            break
        }

        Write-Host "Please type exactly approve or reject." -ForegroundColor Yellow
    }

    $decisionBody = [ordered]@{
        approved = $decision -eq "approve"
        provider = $Provider
        scenario = $Scenario
    }
    $decisionPath = "/api/conversations/$conversationId/tool-confirmations/$($Confirmation.confirmationId)/decisions"
    $response = Invoke-ApiRequest -Method POST -Path $decisionPath -Body $decisionBody
    if ($null -ne $response.ConnectionError -or $response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        Show-ApiError $response
        return
    }

    [void] (Show-ConversationResponse $response)
}

function Send-ConversationMessage {
    param([Parameter(Mandatory = $true)][string] $Message)

    $requestBody = [ordered]@{
        message = $Message
        provider = $Provider
        scenario = $Scenario
    }
    if ($null -ne $conversationId) {
        $requestBody.conversationId = $conversationId
    }

    $response = Invoke-ApiRequest -Method POST -Path "/api/conversations/messages" -Body $requestBody
    if ($null -ne $response.ConnectionError -or $response.StatusCode -lt 200 -or $response.StatusCode -ge 299) {
        Show-ApiError $response
        return
    }

    $hasConfirmation = Show-ConversationResponse $response
    if ($hasConfirmation) {
        Resolve-ToolConfirmation $response.Body.confirmation
    }
}

function Show-Help {
    Write-Host @"
Commands:
  /help                 Show this help.
  /new                  Start a new conversation locally.
  /provider fake        Use the deterministic fake provider and start a new conversation.
  /provider ollama      Use Ollama and start a new conversation.
  /scenario <name>      Set the fake scenario (direct, time, temperature).
  /info                 Show the current local client state.
  /exit                 Exit the client.
"@
}

function Show-Info {
    $conversationState = if ($null -eq $conversationId) { "none" } else { $conversationId }
    $authenticationState = if ([string]::IsNullOrWhiteSpace($apiKey)) { "anonymous" } else { "API key configured" }

    Write-Host "Server: $baseUri"
    Write-Host "Provider: $Provider"
    Write-Host "Scenario: $Scenario"
    Write-Host "Conversation: $conversationState"
    Write-Host "Authentication: $authenticationState"
}

function Handle-Command {
    param([Parameter(Mandatory = $true)][string] $CommandLine)

    $parts = $CommandLine.Trim().Split(" ", 2, [System.StringSplitOptions]::RemoveEmptyEntries)
    $command = $parts[0].ToLowerInvariant()
    $argument = if ($parts.Count -gt 1) { $parts[1].Trim() } else { "" }

    switch ($command) {
        "/help" { Show-Help; return $true }
        "/new" {
            $script:conversationId = $null
            Write-Host "Started a new conversation."
            return $true
        }
        "/provider" {
            $newProvider = $argument.ToLowerInvariant()
            if ($newProvider -notin @("fake", "ollama")) {
                Write-Host "Usage: /provider fake or /provider ollama" -ForegroundColor Yellow
                return $true
            }

            $script:Provider = $newProvider
            $script:conversationId = $null
            Write-Host "Provider set to $Provider. Started a new conversation."
            return $true
        }
        "/scenario" {
            if ($Provider -ne "fake") {
                Write-Host "Scenarios apply only when the fake provider is selected." -ForegroundColor Yellow
                return $true
            }

            if ([string]::IsNullOrWhiteSpace($argument)) {
                Write-Host "Usage: /scenario <name>. Supported scenarios: $($supportedFakeScenarios -join ', ')." -ForegroundColor Yellow
                return $true
            }

            $script:Scenario = $argument.ToLowerInvariant()
            if ($Scenario -notin $supportedFakeScenarios) {
                Write-Host "Scenario set to '$Scenario'. The API will report it as invalid if unsupported." -ForegroundColor Yellow
            }
            else {
                Write-Host "Fake scenario set to $Scenario."
            }

            return $true
        }
        "/info" { Show-Info; return $true }
        "/exit" { return $false }
        default {
            Write-Host "Unknown command. Type /help for available commands." -ForegroundColor Yellow
            return $true
        }
    }
}

try {
    $baseUrlUri = $null
    if (-not [System.Uri]::TryCreate($BaseUrl, [System.UriKind]::Absolute, [ref] $baseUrlUri)) {
        throw "BaseUrl must be an absolute URL."
    }

    $baseUri = $baseUrlUri.AbsoluteUri.TrimEnd("/")
    $apiKey = Get-ApiKey
    $httpClient = [System.Net.Http.HttpClient]::new()

    $health = Invoke-ApiRequest -Method GET -Path "/health"
    if ($null -ne $health.ConnectionError -or $health.StatusCode -ne 200) {
        Show-ApiError $health
        exit 1
    }

    Write-Host "LocalAssistant terminal client"
    Write-Host "Server: $baseUri"
    Write-Host "Provider: $Provider"
    if ($Provider -eq "fake") {
        Write-Host "Fake scenario: $Scenario" -ForegroundColor Yellow
    }
    else {
        Write-Host "Using Ollama. The model is configured by the server." -ForegroundColor Green
    }
    Write-Host "Type /help for commands."

    while ($true) {
        $input = Read-Host "You"
        if ($null -eq $input) {
            continue
        }

        $input = $input.Trim()
        if ([string]::IsNullOrWhiteSpace($input)) {
            continue
        }

        if ($input.StartsWith("/")) {
            if (-not (Handle-Command $input)) {
                break
            }

            continue
        }

        Send-ConversationMessage $input
    }
}
catch {
    Write-Host "Client error: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if ($null -ne $httpClient) {
        $httpClient.Dispose()
    }
}
