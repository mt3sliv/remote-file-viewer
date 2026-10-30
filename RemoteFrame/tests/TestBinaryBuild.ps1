$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Import-Module "$root/packaging/libreoffice/SourceBuild.psm1" -Force
Import-Module "$root/packaging/libreoffice/BinaryBuild.psm1" -Force
$fixture = Join-Path $root ('artifacts/binary-pipeline-test-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $fixture 'office'
New-Item -ItemType Directory -Force "$output/program" | Out-Null
# These are text fixtures, never executed or packaged as an Office runtime.
$archive = Join-Path $fixture 'TEXT-FIXTURE-NOT-AN-INSTALLER.msi'
Set-Content -LiteralPath $archive -Value 'TEXT FIXTURE ONLY'
$lock = (Get-BinaryLock).Value
$lock.sha256 = Get-FileDigest $archive
$lockPath = Join-Path $fixture 'fixture.lock.json'
$lock | ConvertTo-Json | Set-Content -LiteralPath $lockPath
foreach ($file in @('program/soffice.exe','program/mergedlo.dll','program/version.ini','license.txt','NOTICE')) {
    Set-Content -LiteralPath (Join-Path $output $file) -Value "TEXT FIXTURE ONLY: $file"
}
$receipt = @{
    schemaVersion=1; method='upstream-binary'; version=$lock.version; runtimeIdentifier='win-x64'
    lockSha256=(Get-FileDigest $lockPath); archiveSha256=$lock.sha256; archivePath=$archive
    outputDirectory=$output; outputFiles=(Get-EngineInventory $output)
}
$path = Join-Path $fixture 'receipt.json'
function Save-Receipt { $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path }
function Reject([scriptblock]$action, [string]$name) {
    try { & $action | Out-Null } catch { Write-Output "PASS: $name rejected"; return }
    throw "$name was unexpectedly accepted"
}
Save-Receipt
$null = Assert-BinaryBuildReceipt $path $lockPath
Write-Output 'PASS: Binary receipt binds the pinned archive to its exact extracted inventory.'
$receipt.method='source'; Save-Receipt
Reject { Assert-BinaryBuildReceipt $path $lockPath } 'Source output presented as an official binary'
$receipt.method='upstream-binary'; Save-Receipt
Add-Content -LiteralPath "$output/program/mergedlo.dll" -Value 'altered'
Reject { Assert-BinaryBuildReceipt $path $lockPath } 'Changed binary output'
Set-Content -LiteralPath "$output/program/mergedlo.dll" -Value 'TEXT FIXTURE ONLY: program/mergedlo.dll'
Add-Content -LiteralPath $archive -Value 'altered archive'
Reject { Assert-BinaryBuildReceipt $path $lockPath } 'Changed archive checksum'
Reject { & "$root/packaging/BuildWindowsRuntime.ps1" -EngineMode Source -BinaryBuildReceipt $path -VcRedistDirectory $fixture -MicrosoftNoticesPath $archive } 'Binary receipt in source mode'
Reject { & "$root/packaging/BuildWindowsRuntime.ps1" -EngineMode Binary -SourceBuildReceipt $path -VcRedistDirectory $fixture -MicrosoftNoticesPath $archive } 'Source receipt in binary mode'
Write-Output 'All binary pipeline checks passed.'

Import-Module "$root/packaging/libreoffice/BinaryDownload.psm1" -Force
$download = "$fixture/download.part"
$content = 'VERIFIED DOWNLOAD FIXTURE'
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content)))
$state = @{ count=0 }
$transport = {
    param($url,$path)
    $state.count++
    if ($state.count -eq 1) { throw 'Simulated stalled connection' }
    [IO.File]::WriteAllText($path,$content)
}.GetNewClosure()
Invoke-VerifiedBinaryDownload -Uri 'https://example.invalid/fixture' -Destination $download -Sha256 $digest -Transport $transport -RetryDelaySeconds 0
if ($state.count -ne 2 -or (Get-FileDigest $download) -ne $digest) { throw 'Download retry did not preserve checksum verification.' }
$state.count = 0
$badTransport = { param($url,$path) $state.count++; [IO.File]::WriteAllText($path,'CORRUPTED DOWNLOAD') }.GetNewClosure()
Reject { Invoke-VerifiedBinaryDownload -Uri 'https://example.invalid/fixture' -Destination $download -Sha256 $digest -Transport $badTransport -RetryDelaySeconds 0 } 'Repeated checksum failures'
if ($state.count -ne 3) { throw 'Download attempts were not bounded.' }
$start = [Diagnostics.ProcessStartInfo]::new('pwsh')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
foreach ($argument in '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30') { $start.ArgumentList.Add($argument) }
$child = [Diagnostics.Process]::Start($start)
try {
    Reject { Wait-BinaryExtraction -Process $child -TimeoutSeconds 1 } 'Stalled extraction'
    if (-not $child.HasExited) { throw 'Timed-out extraction process was not stopped.' }
} finally { if (-not $child.HasExited) { $child.Kill($true) }; $child.Dispose() }
Write-Output 'PASS: Download retries are bounded, checksums remain mandatory, and stalled extraction is terminated.'
