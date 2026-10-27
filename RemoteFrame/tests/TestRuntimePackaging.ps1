$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
$payload = Join-Path $remoteFrameRoot 'artifacts/test-runtime'
$feed = Join-Path $remoteFrameRoot 'artifacts/test-packages'
$consumer = Join-Path $PSScriptRoot 'RemoteFrame.RuntimePackageSmoke'
$publish = Join-Path $remoteFrameRoot 'artifacts/runtime-fixture-publish'

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Force "$payload/office/program", "$payload/worker", "$payload/notices" | Out-Null
$inventory = @{}
foreach ($relative in @('office/program/soffice.exe', 'worker/worker.exe', 'notices/LICENSE.txt', 'office/program/extensionless', 'office/program/.dir')) {
    $fixturePath = Join-Path $payload $relative
    Set-Content -LiteralPath $fixturePath -Value 'TEST FIXTURE ONLY - never execute'
    $inventory[$relative] = (Get-FileHash -LiteralPath $fixturePath -Algorithm SHA256).Hash
}
@{
    schemaVersion = 1
    runtimeIdentifier = 'win-x64'
    version = 'test-fixture'
    sourceRevision = 'test-fixture-not-upstream'
    engine = 'office/program/soffice.exe'
    worker = 'worker/worker.exe'
    licenses = @('notices/LICENSE.txt')
    files = $inventory
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $payload 'runtime.json')

Invoke-DotNet run --project (Join-Path $PSScriptRoot 'RemoteFrame.RuntimeTests')
Invoke-DotNet pack (Join-Path $remoteFrameRoot 'packaging/RemoteFrame.Office.Runtime.csproj') '-p:OfficeRuntimeIdentifier=win-x64' "-p:OfficePayloadRoot=$payload" '-p:Version=0.0.0-test-fixture.3' -o $feed
Invoke-DotNet restore $consumer --configfile (Join-Path $PSScriptRoot 'NuGetFixtures.Config') --packages (Join-Path $remoteFrameRoot 'artifacts/runtime-fixture-cache')
Invoke-DotNet run --project $consumer --no-restore
Invoke-DotNet publish $consumer --no-restore -o $publish
Invoke-DotNet (Join-Path $publish 'RemoteFrame.RuntimePackageSmoke.dll')


