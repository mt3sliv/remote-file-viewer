Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SourceBuild.psm1')

function Get-BinaryLock([string]$LockPath = "$PSScriptRoot/binary.lock.json") {
    $path = $LockPath
    $value = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($value.schemaVersion -ne 1 -or $value.runtimeIdentifier -ne 'win-x64' -or
        $value.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $value.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        ([Uri]$value.url).Scheme -ne 'https' -or ([Uri]$value.url).Host -ne 'downloadarchive.documentfoundation.org') {
        throw 'Invalid pinned binary distribution lock.'
    }
    [pscustomobject]@{ Path=$path; Value=$value; Hash=(Get-FileDigest $path) }
}

function Assert-BinaryBuildReceipt([string]$ReceiptPath, [string]$LockPath = "$PSScriptRoot/binary.lock.json") {
    $lock = Get-BinaryLock $LockPath
    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json -AsHashtable
    if ($receipt.schemaVersion -ne 1 -or $receipt.method -ne 'upstream-binary' -or
        $receipt.lockSha256 -ne $lock.Hash -or $receipt.archiveSha256 -ne $lock.Value.sha256 -or
        $receipt.version -ne $lock.Value.version -or $receipt.runtimeIdentifier -ne 'win-x64') {
        throw 'Binary receipt does not match the pinned upstream distribution.'
    }
    if ((Get-FileDigest $receipt.archivePath) -ne $lock.Value.sha256) { throw 'Binary archive checksum changed.' }
    $output = [IO.Path]::GetFullPath($receipt.outputDirectory)
    $actual = Get-EngineInventory $output
    if ($actual.Count -ne $receipt.outputFiles.Count) { throw 'Binary output inventory changed.' }
    foreach ($file in $actual.Keys) {
        if ($actual[$file] -ne $receipt.outputFiles[$file]) { throw "Binary output changed: $file" }
    }
    foreach ($required in @('program/soffice.exe','program/mergedlo.dll','program/version.ini','license.txt','NOTICE')) {
        if (-not $actual.Contains($required)) { throw "Binary distribution is missing: $required" }
    }
    [pscustomobject]@{ Lock=$lock; Receipt=$receipt; OutputDirectory=$output }
}
Export-ModuleMember -Function Get-BinaryLock,Assert-BinaryBuildReceipt
