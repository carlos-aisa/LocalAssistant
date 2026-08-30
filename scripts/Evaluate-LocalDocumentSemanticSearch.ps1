[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $EmbeddingModel,

    [ValidateNotNullOrEmpty()]
    [string] $Endpoint = "http://localhost:11434",

    [ValidateRange(1, 100)]
    [int] $Limit = 3,

    [ValidateRange(-1, 1)]
    [double] $MinimumSimilarity = 0.78,

    [string] $OutputPath = "artifacts/local-document-semantic-search.json"
)

$ErrorActionPreference = "Stop"

dotnet run --configuration Release --project src/LocalAssistant.DocumentSearchEvaluation -- `
    --endpoint $Endpoint `
    --model $EmbeddingModel `
    --limit $Limit `
    --minimum-similarity $MinimumSimilarity `
    --output $OutputPath
