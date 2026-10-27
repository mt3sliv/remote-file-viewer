$ErrorActionPreference = 'Stop'
$remoteFrameRoot = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $remoteFrameRoot ('artifacts/source-pipeline-test-' + [Guid]::NewGuid().ToString('N'))
$recipeDir = Join-Path $fixture 'recipe'
$origin = Join-Path $fixture 'upstream-fixture'
$buildRoot = Join-Path $fixture 'build'
New-Item -ItemType Directory -Force $recipeDir, $origin | Out-Null
Get-ChildItem -LiteralPath "$remoteFrameRoot/packaging/libreoffice" -Force | Copy-Item -Destination $recipeDir -Recurse
Import-Module "$recipeDir/SourceBuild.psm1" -Force

function Reject([scriptblock]$Action, [string]$Name) {
    try { & $Action | Out-Null } catch { Write-Output "PASS: $Name rejected"; return }
    throw "$Name was unexpectedly accepted"
}

# This is a small local Git/text fixture, not a LibreOffice build or executable engine.
$null = Invoke-SourceGit $origin @('init','-b','main')
$null = Invoke-SourceGit $origin @('config','core.autocrlf','false')
Set-Content -LiteralPath "$origin/.gitignore" -Value "instdir/`nautogen.input"
[IO.File]::WriteAllText("$origin/engine-source.txt", "baseline source fixture`n")
$null = Invoke-SourceGit $origin @('add','.')
$null = Invoke-SourceGit $origin @('-c','user.name=RemoteFrame Tests','-c','user.email=test@example.invalid','commit','-m','Fixture baseline')
$commit = Invoke-SourceGit $origin @('rev-parse','HEAD')
$lock = Get-Content "$recipeDir/source.lock.json" -Raw | ConvertFrom-Json
$lock.repository = $origin
$lock.ref = 'refs/heads/main'
$lock.commit = $commit
$lock | ConvertTo-Json | Set-Content "$recipeDir/source.lock.json"

& "$recipeDir/Build-LibreOffice.ps1" -Phase Fetch -BuildRoot $buildRoot
$first = Get-SourceRecipe $recipeDir
& "$recipeDir/Build-LibreOffice.ps1" -Phase Fetch -BuildRoot $buildRoot
Write-Output 'PASS: Locked local source prepares and reuses its verified checkout'

[IO.File]::WriteAllText("$origin/engine-source.txt", "patched source fixture`n")
$patch = (Invoke-SourceGit $origin @('diff','--binary')) + "`n"
[IO.File]::WriteAllText("$recipeDir/patches/0001-fixture.patch", $patch)
[IO.File]::WriteAllText("$origin/engine-source.txt", "baseline source fixture`n")
Set-Content "$recipeDir/patches/series" -Value '0001-fixture.patch'
$recipe = Get-SourceRecipe $recipeDir
if ($recipe.Fingerprint -eq $first.Fingerprint) { throw 'Patch changes did not change the recipe identity' }
& "$recipeDir/Build-LibreOffice.ps1" -Phase Fetch -BuildRoot $buildRoot
$checkout = Join-Path (Join-Path $buildRoot $recipe.Fingerprint.Substring(0,16)) 'core'
if ((Get-Content "$checkout/engine-source.txt" -Raw).Trim() -ne 'patched source fixture') { throw 'Ordered source patch was not applied' }
Write-Output 'PASS: Ordered patch application produces a different, recorded source tree'

$output = Join-Path $checkout 'instdir'
New-Item -ItemType Directory -Force "$output/program" | Out-Null
foreach ($relative in @('program/soffice.exe','program/mergedlo.dll','program/version.ini','license.txt','NOTICE')) {
    Set-Content -LiteralPath (Join-Path $output $relative) -Value "TEXT FIXTURE ONLY: $relative"
}
$receipt = @{
    schemaVersion=1; method='source'; runtimeIdentifier='win-x64'; upstreamRevision=$commit
    recipeFingerprint=$recipe.Fingerprint; sourceTree=(Invoke-SourceGit $checkout @('write-tree'))
    version=$recipe.Lock.version; profileSha256=$recipe.ProfileHash; patchesSha256=$recipe.PatchHash
    sourceDirectory=$checkout; outputDirectory=$output; buildSucceeded=$true; testsSucceeded=$true
    outputFiles=(Get-EngineInventory $output)
}
$receiptPath = Join-Path $fixture 'fixture-receipt.json'
function Save-Receipt { $receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $receiptPath }
Save-Receipt
$null = Assert-SourceBuildReceipt $receiptPath $recipeDir
Write-Output 'PASS: Receipt binds the tested source tree to its exact output inventory'

$receipt.testsSucceeded=$false; Save-Receipt
Reject { Assert-SourceBuildReceipt $receiptPath $recipeDir } 'Failed upstream checks'
$receipt.testsSucceeded=$true; Save-Receipt
$receipt.method='installed'; Save-Receipt
Reject { Assert-SourceBuildReceipt $receiptPath $recipeDir } 'Installed-binary provenance'
$receipt.method='source'; Save-Receipt
Add-Content "$output/program/mergedlo.dll" -Value 'tampering'
Reject { Assert-SourceBuildReceipt $receiptPath $recipeDir } 'Changed native output'
Set-Content "$output/program/mergedlo.dll" -Value 'TEXT FIXTURE ONLY: program/mergedlo.dll'
[IO.File]::WriteAllText("$checkout/engine-source.txt", "unrecorded edit`n")
Reject { Assert-SourceBuildReceipt $receiptPath $recipeDir } 'Unrecorded source edit'
[IO.File]::WriteAllText("$checkout/engine-source.txt", "patched source fixture`n")
Set-Content "$checkout/untracked-source.txt" -Value 'not in a patch'
Reject { Assert-SourceBuildReceipt $receiptPath $recipeDir } 'Untracked source input'
Remove-Item -LiteralPath "$checkout/untracked-source.txt"
Add-Content "$recipeDir/profiles/win-x64.args" -Value '--enable-debug'
Reject { Assert-SourceBuildReceipt $receiptPath $recipeDir } 'Changed build profile'
Set-Content "$recipeDir/patches/series" -Value "0001-fixture.patch`n0001-fixture.patch"
Reject { Get-SourceRecipe $recipeDir } 'Duplicate patch'
Set-Content "$recipeDir/patches/series" -Value '../outside.patch'
Reject { Get-SourceRecipe $recipeDir } 'Escaping patch path'
Write-Output "Source-pipeline fixture checks passed. Fixtures retained at $fixture"

