param(
    [ValidateSet('Preflight','Fetch','Build','Package')][string]$Phase = 'Preflight',
    [string]$BuildRoot = (Join-Path $PSScriptRoot '../../artifacts/libreoffice-source'),
    [string]$BashPath = 'C:/cygwin64/bin/bash.exe',
    [ValidateRange(1,256)][int]$Jobs = [Math]::Max(1, [Environment]::ProcessorCount - 2),
    [string]$VcRedistDirectory,
    [string]$MicrosoftNoticesPath,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+-source\.[0-9]+$')][string]$PackageVersion = '0.3.0-source.1'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'SourceBuild.psm1') -Force
$recipe = Get-SourceRecipe
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ($BuildRoot -match '\s') { throw 'Use a source-build directory without spaces.' }
$jobRoot = Join-Path $BuildRoot $recipe.Fingerprint.Substring(0,16)
$checkout = Join-Path $jobRoot 'core'
$marker = Join-Path $jobRoot 'prepared.json'
$receiptPath = Join-Path $jobRoot 'source-build.json'
$remoteFrameRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))

function Test-Prerequisites {
    $missing = [Collections.Generic.List[string]]::new()
    if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') { $missing.Add('A Windows x64 source-build machine') }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { $missing.Add('Git') }
    if (-not (Test-Path -LiteralPath $BashPath -PathType Leaf)) { $missing.Add("Cygwin build shell: $BashPath (Git Bash is not supported)") }
    $vcvars = Join-Path $env:ProgramFiles 'Microsoft Visual Studio/2022/Community/VC/Auxiliary/Build/vcvars64.bat'
    if (-not (Test-Path -LiteralPath $vcvars)) { $missing.Add('Visual Studio 2022 Community C++ tools (also used by the existing input-helper build)') }
    if ($missing.Count) { throw ("Source build prerequisites are missing:`n - " + ($missing -join "`n - ")) }
    & $BashPath --login (Join-Path $PSScriptRoot 'build-engine.sh').Replace('\','/') --preflight
    if ($LASTEXITCODE -ne 0) { throw 'The Cygwin build-tool preflight failed. No source was downloaded.' }
    Write-Output "Preflight passed. Source revision: $($recipe.Lock.commit)"
    Write-Output "Build directory: $jobRoot"
}

function Prepare-Source {
    if (Test-Path -LiteralPath $marker) {
        $prepared = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($prepared.recipeFingerprint -ne $recipe.Fingerprint -or
            (Invoke-SourceGit $checkout @('rev-parse','HEAD')) -ne $recipe.Lock.commit -or
            (Invoke-SourceGit $checkout @('write-tree')) -ne $prepared.sourceTree) { throw 'Prepared source differs from the locked recipe. Use a fresh BuildRoot; no checkout will be reset.' }
        $null = Invoke-SourceGit $checkout @('diff','--exit-code')
        if (Invoke-SourceGit $checkout @('ls-files','--others','--exclude-standard')) { throw 'The source checkout contains untracked files outside the patch series.' }
        return
    }
    if (Test-Path -LiteralPath $checkout) { throw 'An unverified or interrupted checkout exists. Use a fresh BuildRoot; existing files will not be overwritten.' }
    New-Item -ItemType Directory -Force $checkout | Out-Null
    $null = Invoke-SourceGit $checkout @('init')
    $null = Invoke-SourceGit $checkout @('config','core.autocrlf','false')
    $null = Invoke-SourceGit $checkout @('config','core.filemode','false')
    $null = Invoke-SourceGit $checkout @('remote','add','origin',$recipe.Lock.repository)
    Write-Output 'Fetching the locked upstream release; this can require a substantial download.'
    $null = Invoke-SourceGit $checkout @('fetch','--depth=1','--filter=blob:none','origin',$recipe.Lock.ref)
    if ((Invoke-SourceGit $checkout @('rev-parse','FETCH_HEAD^{commit}')) -ne $recipe.Lock.commit) { throw 'Upstream tag does not match the immutable source lock.' }
    $null = Invoke-SourceGit $checkout @('checkout','--detach',$recipe.Lock.commit)
    $null = Invoke-SourceGit $checkout @('submodule','update','--init','--recursive','--depth','1')
    foreach ($patch in $recipe.Patches) {
        $null = Invoke-SourceGit $checkout @('apply','--check','--index',$patch.Path)
        $null = Invoke-SourceGit $checkout @('apply','--index',$patch.Path)
    }
    @{ recipeFingerprint=$recipe.Fingerprint; sourceTree=(Invoke-SourceGit $checkout @('write-tree')) } |
        ConvertTo-Json | Set-Content -LiteralPath $marker
}

switch ($Phase) {
    'Preflight' { Test-Prerequisites }
    'Fetch' { Prepare-Source; Write-Output "Prepared source: $checkout" }
    'Build' {
        Test-Prerequisites
        Prepare-Source
        # A failed rebuild must never leave a previous successful receipt usable.
        if (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }
        $argsFile = Join-Path $jobRoot 'configure.args'
        Copy-Item -LiteralPath $recipe.Profile -Destination $argsFile -Force
        & $BashPath --login (Join-Path $PSScriptRoot 'build-engine.sh').Replace('\','/') $checkout $argsFile $Jobs 2>&1 |
            Tee-Object -FilePath (Join-Path $jobRoot 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'LibreOffice configure/build/check failed. No successful receipt was written; inspect build.log.' }
        if ((Get-FileDigest $argsFile) -ne $recipe.ProfileHash) { throw 'Configure arguments changed during the build.' }
        $prepared = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ((Invoke-SourceGit $checkout @('write-tree')) -ne $prepared.sourceTree) { throw 'The patch tree changed during the build.' }
        $null = Invoke-SourceGit $checkout @('diff','--exit-code')
        $output = Join-Path $checkout 'instdir'
        $receipt = [ordered]@{
            schemaVersion=1; method='source'; runtimeIdentifier=$recipe.Lock.runtimeIdentifier
            upstreamRevision=$recipe.Lock.commit; version=$recipe.Lock.version; sourceTree=$prepared.sourceTree
            recipeFingerprint=$recipe.Fingerprint; profileSha256=$recipe.ProfileHash; patchesSha256=$recipe.PatchHash
            sourceDirectory=$checkout; outputDirectory=$output; buildSucceeded=$true; testsSucceeded=$true
            completedUtc=[DateTime]::UtcNow.ToString('O'); jobs=$Jobs
            outputFiles=(Get-EngineInventory $output)
        }
        $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath
        $null = Assert-SourceBuildReceipt $receiptPath
        Write-Output "Verified source-build receipt: $receiptPath"
    }
    'Package' {
        if (-not $VcRedistDirectory -or -not $MicrosoftNoticesPath) { throw 'Package requires VcRedistDirectory and MicrosoftNoticesPath build inputs.' }
        & (Join-Path $remoteFrameRoot 'packaging/BuildWindowsRuntime.ps1') -SourceBuildReceipt $receiptPath -VcRedistDirectory $VcRedistDirectory -MicrosoftNoticesPath $MicrosoftNoticesPath -PackageVersion $PackageVersion
    }
}
