param([string]$CacheRoot = "$PSScriptRoot/../../artifacts/libreoffice-binary")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Preparing the Windows binary runtime requires Windows.' }
Import-Module (Join-Path $PSScriptRoot 'SourceBuild.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'BinaryBuild.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'BinaryDownload.psm1') -Force
$lock = Get-BinaryLock
$CacheRoot = [IO.Path]::GetFullPath($CacheRoot)
New-Item -ItemType Directory -Force $CacheRoot | Out-Null
$archive = Join-Path $CacheRoot ([IO.Path]::GetFileName(([Uri]$lock.Value.url).AbsolutePath))
if (-not (Test-Path -LiteralPath $archive)) {
    $download = "$archive.download"
    Invoke-VerifiedBinaryDownload -Uri $lock.Value.url -Destination $download -Sha256 $lock.Value.sha256
    Move-Item -LiteralPath $download -Destination $archive
}
if ((Get-FileDigest $archive) -ne $lock.Value.sha256) { throw 'Cached LibreOffice archive failed checksum verification; it will not be used.' }
$job = Join-Path $CacheRoot $lock.Value.sha256.Substring(0,16)
$receipt = Join-Path $job 'binary-build.json'
if (Test-Path -LiteralPath $receipt) {
    Write-Host 'Verifying cached LibreOffice archive and extracted file hashes...'
    $null = Assert-BinaryBuildReceipt $receipt
    Write-Output $receipt
    return
}
# Never overwrite an interrupted extraction; each attempt gets its own directory.
$attempt = 'extract-' + [Guid]::NewGuid().ToString('N')
$extract = Join-Path $job $attempt
New-Item -ItemType Directory -Force $extract | Out-Null
$log = Join-Path $job "$attempt.log"
# /a creates an administrative image; it does not install/register Office on this host.
Write-Host "Extracting the verified LibreOffice MSI (20-minute limit). Log: $log"
$process = Start-Process -FilePath "$env:WINDIR/System32/msiexec.exe" -WindowStyle Hidden -PassThru -ArgumentList @('/a',"`"$archive`"",'/qn',"TARGETDIR=`"$extract`"",'/L*v',"`"$log`"")
try {
    $exitCode = Wait-BinaryExtraction -Process $process
    if ($exitCode -ne 0) { throw "LibreOffice administrative extraction failed ($exitCode); see $log" }
} finally { $process.Dispose() }
$engines = @(Get-ChildItem -LiteralPath $extract -Recurse -Filter soffice.exe -File)
if ($engines.Count -ne 1 -or $engines[0].Directory.Name -ne 'program') { throw 'Cannot identify the extracted LibreOffice directory.' }
$output = $engines[0].Directory.Parent.FullName
Write-Host 'Extraction finished; recording and validating the runtime inventory...'
# Administrative MSI databases are extraction metadata, not part of the runtime.
foreach ($database in Get-ChildItem -LiteralPath $output -Filter '*.msi' -File) {
    Move-Item -LiteralPath $database.FullName -Destination (Join-Path $job "$attempt-$($database.Name)")
}
$result = [ordered]@{
    schemaVersion=1; method='upstream-binary'; version=$lock.Value.version; runtimeIdentifier='win-x64'
    lockSha256=$lock.Hash; archiveSha256=$lock.Value.sha256; archivePath=$archive
    outputDirectory=$output; preparedUtc=[DateTime]::UtcNow.ToString('O'); outputFiles=(Get-EngineInventory $output)
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receipt
$null = Assert-BinaryBuildReceipt $receipt
Write-Output $receipt
