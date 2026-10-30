param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$RunId, [string]$Token = $env:GITHUB_TOKEN,
    [scriptblock]$ReadApi)
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $RunId -notmatch '^[1-9][0-9]*$' -or -not $Tag.StartsWith('v')) { throw 'Expected repository, version tag and numeric CI run ID.' }
$version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Tag.Substring(1)
function Read-GitHub([string]$path) {
    if ($ReadApi) { return & $ReadApi $path }
    if (-not $Token) { throw 'A read-only GitHub token is required to verify recovery provenance.' }
    Invoke-RestMethod "https://api.github.com/repos/$Repository$path" -Headers @{Authorization="Bearer $Token";Accept='application/vnd.github+json'} -TimeoutSec 30
}
$commit = (Read-GitHub ('/commits/' + [Uri]::EscapeDataString($Tag))).sha
if ($commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'The release tag did not resolve to a commit.' }
$workflow = Read-GitHub '/actions/workflows/ci.yml'
$run = Read-GitHub "/actions/runs/$RunId"
if ($run.head_sha -ne $commit -or $run.status -ne 'completed' -or $run.conclusion -ne 'success' -or
    $run.workflow_id -ne $workflow.id -or $run.head_repository.full_name -ne $Repository -or
    $run.event -notin @('push','workflow_dispatch')) { throw 'Recovery requires a successful repository CI run for the exact release-tag commit.' }
$artifacts = Read-GitHub "/actions/runs/$RunId/artifacts"
$matches = @($artifacts.artifacts | Where-Object { $_.name -eq 'nuget-release' -and -not $_.expired })
if ($matches.Count -ne 1) { throw 'The successful CI run must retain one unexpired nuget-release artifact.' }
[pscustomobject]@{ Version=$version; Commit=$commit; RunId=$RunId }
