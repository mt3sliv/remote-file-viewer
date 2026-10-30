$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sha = 'a' * 40
$state = @{
    commit=@{sha=$sha}; workflow=@{id=7}
    run=@{head_sha=$sha;status='completed';conclusion='success';workflow_id=7;head_repository=@{full_name='owner/repo'};event='push'}
    artifacts=@{artifacts=@(@{name='nuget-release';expired=$false})}
}
$read = {
    param($path)
    switch ($path) {
        '/commits/v0.3.0-preview.11' { $state.commit }
        '/actions/workflows/ci.yml' { $state.workflow }
        '/actions/runs/42' { $state.run }
        '/actions/runs/42/artifacts' { $state.artifacts }
        default { throw "Unexpected API request: $path" }
    }
}.GetNewClosure()
function Resolve { & "$root/packaging/Resolve-ReleaseRecovery.ps1" -Repository owner/repo -Tag v0.3.0-preview.11 -RunId 42 -ReadApi $read }
function Reject([scriptblock]$action,[string]$name) {
    try { & $action | Out-Null } catch { Write-Output "PASS: $name rejected"; return }
    throw "$name was accepted unexpectedly."
}
$result = Resolve
if ($result.Commit -ne $sha -or $result.Version -ne '0.3.0-preview.11' -or $result.RunId -ne '42') { throw 'Recovery did not resolve the exact tag and CI artifact.' }
$state.run.head_sha = 'b' * 40
Reject { Resolve } 'Different CI commit'
$state.run.head_sha = $sha
$state.run.workflow_id = 8
Reject { Resolve } 'Different workflow'
$state.run.workflow_id = 7
$state.run.head_repository.full_name = 'other/repo'
Reject { Resolve } 'Different source repository'
$state.run.head_repository.full_name = 'owner/repo'
$state.run.event = 'pull_request'
Reject { Resolve } 'Pull request artifact'
$state.run.event = 'push'
$state.run.conclusion = 'failure'
Reject { Resolve } 'Failed CI run'
$state.run.conclusion = 'success'
$state.artifacts.artifacts[0].expired = $true
Reject { Resolve } 'Expired artifact'
Write-Output 'PASS: Release recovery requires successful repository CI and an unexpired artifact for the exact version-tag commit.'
