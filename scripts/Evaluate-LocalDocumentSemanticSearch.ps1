[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $EmbeddingModel,

    [ValidateNotNullOrEmpty()]
    [string] $Endpoint = "http://localhost:11434",

    [ValidateRange(1, 100)]
    [int] $Limit = 3,

    [string] $OutputPath = "artifacts/local-document-semantic-search.json"
)

$ErrorActionPreference = "Stop"

dotnet run --configuration Release --project src/LocalAssistant.DocumentSearchEvaluation -- `
    --endpoint $Endpoint `
    --model $EmbeddingModel `
    --limit $Limit `
    --output $OutputPath
