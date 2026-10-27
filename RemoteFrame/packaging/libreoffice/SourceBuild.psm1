Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TextHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text)))
}
function Get-FileDigest([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Invoke-SourceGit([string]$Directory, [string[]]$Arguments) {
    $result = & git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Source Git operation failed: $($result -join [Environment]::NewLine)" }
    ($result -join "`n").Trim()
}
function Get-SourceRecipe([string]$RecipeDirectory = $PSScriptRoot) {
    $lockPath = Join-Path $RecipeDirectory 'source.lock.json'
    $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
    if ($lock.schemaVersion -ne 1 -or $lock.commit -notmatch '^[a-f0-9]{40}$' -or $lock.runtimeIdentifier -ne 'win-x64') {
        throw 'Invalid or unsupported LibreOffice source lock.'
    }
    $profile = Join-Path $RecipeDirectory $lock.profile
    $series = Join-Path $RecipeDirectory $lock.patchSeries
    $patches = @()
    foreach ($line in Get-Content -LiteralPath $series) {
        $name = $line.Trim()
        if (-not $name -or $name.StartsWith('#')) { continue }
        if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.patch$' -or @($patches | ForEach-Object Name) -contains $name) {
            throw "Invalid or duplicate patch entry: $name"
        }
        $path = Join-Path (Split-Path $series -Parent) $name
        $patches += [pscustomobject]@{ Name=$name; Path=$path; Sha256=(Get-FileDigest $path) }
    }
    foreach ($file in Get-ChildItem -LiteralPath (Split-Path $series -Parent) -Filter '*.patch') {
        if (@($patches | ForEach-Object Name) -notcontains $file.Name) { throw "Patch is not listed in series: $($file.Name)" }
    }
    $profileHash = Get-FileDigest $profile
    $lockHash = Get-FileDigest $lockPath
    $patchHash = Get-TextHash ((@((Get-FileDigest $series)) + @($patches | ForEach-Object { "$($_.Name):$($_.Sha256)" })) -join "`n")
    $driverHash = Get-FileDigest (Join-Path $RecipeDirectory 'Build-LibreOffice.ps1')
    $shellHash = Get-FileDigest (Join-Path $RecipeDirectory 'build-engine.sh')
    $moduleHash = Get-FileDigest (Join-Path $RecipeDirectory 'SourceBuild.psm1')
    $fingerprint = Get-TextHash "$lockHash`n$profileHash`n$patchHash`n$driverHash`n$shellHash`n$moduleHash"
    [pscustomobject]@{
        Lock=$lock; LockPath=$lockPath; Profile=$profile; Series=$series; Patches=$patches
        LockHash=$lockHash; ProfileHash=$profileHash; PatchHash=$patchHash; Fingerprint=$fingerprint
    }
}
function Get-EngineInventory([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory)
    $files = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File | Sort-Object FullName) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Stage regular native files, not symbolic links.' }
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        $files[$relative] = Get-FileDigest $file.FullName
    }
    $files
}
function Assert-SourceBuildReceipt([string]$ReceiptPath, [string]$RecipeDirectory = $PSScriptRoot) {
    $recipe = Get-SourceRecipe $RecipeDirectory
    $receipt = Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json -AsHashtable
    if ($receipt.schemaVersion -ne 1 -or $receipt.method -ne 'source' -or -not $receipt.buildSucceeded -or -not $receipt.testsSucceeded -or
        $receipt.recipeFingerprint -ne $recipe.Fingerprint -or $receipt.upstreamRevision -ne $recipe.Lock.commit -or
        $receipt.version -ne $recipe.Lock.version -or $receipt.profileSha256 -ne $recipe.ProfileHash -or $receipt.patchesSha256 -ne $recipe.PatchHash -or
        $receipt.runtimeIdentifier -ne $recipe.Lock.runtimeIdentifier) { throw 'A successful build/test receipt for the current source recipe is required.' }
    $checkout = [IO.Path]::GetFullPath($receipt.sourceDirectory)
    $output = [IO.Path]::GetFullPath($receipt.outputDirectory)
    if ($output -ne [IO.Path]::GetFullPath((Join-Path $checkout 'instdir'))) { throw 'Engine output must be the recorded source checkout instdir.' }
    if ((Invoke-SourceGit $checkout @('rev-parse','HEAD')) -ne $recipe.Lock.commit -or
        (Invoke-SourceGit $checkout @('write-tree')) -ne $receipt.sourceTree) { throw 'Source revision or applied patch tree changed after the build.' }
    $null = Invoke-SourceGit $checkout @('diff','--exit-code')
    if (Invoke-SourceGit $checkout @('ls-files','--others','--exclude-standard')) { throw 'Untracked source files are not part of the tested patch tree.' }
    $actual = Get-EngineInventory $output
    if ($actual.Count -ne $receipt.outputFiles.Count) { throw 'Source-build output inventory changed.' }
    foreach ($name in $actual.Keys) {
        if ($actual[$name] -ne $receipt.outputFiles[$name]) { throw "Source-build output changed: $name" }
    }
    foreach ($required in @('program/soffice.exe','program/mergedlo.dll','program/version.ini','license.txt','NOTICE')) {
        if (-not $actual.Contains($required)) { throw "Source build is missing: $required" }
    }
    [pscustomobject]@{ Recipe=$recipe; Receipt=$receipt; OutputDirectory=$output }
}
Export-ModuleMember -Function Get-SourceRecipe,Get-EngineInventory,Assert-SourceBuildReceipt,Invoke-SourceGit,Get-FileDigest
