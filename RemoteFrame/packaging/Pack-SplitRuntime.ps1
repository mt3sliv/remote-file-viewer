param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/packages",
    [ValidateRange(1,180000000)][long]$PartBudgetBytes = 180000000,
    [string]$RuntimeIdentifier = 'win-x64'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Version = & "$PSScriptRoot/Get-ReleaseVersion.ps1" $Version
if ($RuntimeIdentifier -notmatch '^(win|linux)-(x64|arm64)$') { throw 'Unsupported runtime identifier.' }
$Payload = [IO.Path]::GetFullPath($Payload)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$root = Split-Path $PSScriptRoot -Parent
$id = "RemoteFrame.Office.Runtime.$RuntimeIdentifier"
if (Test-Path "$OutputDirectory/$id.$Version.nupkg") { throw 'Choose a fresh package version; package versions are immutable.' }
& dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- $Payload $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { throw 'Runtime validation failed before splitting.' }
$work = Join-Path $root ('artifacts/split-runtime/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work,$OutputDirectory | Out-Null
function Xml([string]$value) { [Security.SecurityElement]::Escape($value) }
@"
<configuration><packageSources><clear/><add key="RemoteFrameLocal" value="$(Xml $OutputDirectory)"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
"@ | Set-Content "$work/NuGet.Config"
function Pack([string]$project) {
    & dotnet pack $project -c Release -o $OutputDirectory "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "Failed to pack $project" }
}
# Use uncompressed byte budgets: even incompressible files stay comfortably below
# nuget.org's 250 MB limit. Never remove engine files or flatten the runtime tree.
$parts = [Collections.Generic.List[object]]::new()
$files = [Collections.Generic.List[object]]::new()
$bytes = 0L
foreach ($file in Get-ChildItem -LiteralPath $Payload -Recurse -Force -File | Sort-Object FullName) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Runtime symlinks are not supported.' }
    if ($file.Length -gt $PartBudgetBytes) { throw "A runtime file exceeds the part budget: $($file.Name)" }
    if ($files.Count -gt 0 -and $bytes + $file.Length -gt $PartBudgetBytes) {
        $parts.Add(@($files.ToArray())); $files.Clear(); $bytes = 0L
    }
    $files.Add($file); $bytes += $file.Length
}
if ($files.Count -gt 0) { $parts.Add(@($files.ToArray())) }
if ($parts.Count -eq 0) { throw 'Empty runtime payload.' }
$license = Get-Content "$root/../LICENSE" -Raw
$license += "`n`nThe MIT license above applies to RemoteFrame code. Bundled third-party components retain their own licenses. Full license texts and third-party notices are included in this package at the following paths and remain in the deployed runtime at their original paths:`n"
$licenseItems = [Collections.Generic.List[string]]::new()
$runtimeManifest = Get-Content "$Payload/runtime.json" -Raw | ConvertFrom-Json
foreach ($relative in $runtimeManifest.licenses | Sort-Object -Unique) {
    $notice = Get-Item -LiteralPath (Join-Path $Payload $relative)
    $license += "`nlicenses/$relative"
    $directory = [IO.Path]::GetDirectoryName($relative).Replace('\','/')
    $licenseItems.Add(('<None Include="{0}" Pack="true" PackagePath="licenses/{1}/" />' -f (Xml $notice.FullName),(Xml $directory)))
}
[IO.File]::WriteAllText("$work/package-license.txt",$license)
if ((Get-Item "$work/package-license.txt").Length -ge 1000000) { throw 'NuGet license metadata must be below 1 MB.' }
[IO.File]::WriteAllText("$work/_._",'')
Copy-Item "$PSScriptRoot/README.md" "$work/README.md"
$commonItems = '<None Include="_._" Pack="true" PackagePath="lib/net10.0/" /><None Include="README.md" Pack="true" PackagePath="/" />'
$commonItems += $licenseItems -join "`n"
$dependencies = [Collections.Generic.List[string]]::new()
for ($index = 0; $index -lt $parts.Count; $index++) {
    $partId = "$id.Part$('{0:D3}' -f ($index + 1))"
    if (Test-Path "$OutputDirectory/$partId.$Version.nupkg") { throw "Package already exists: $partId" }
    $items = foreach ($file in $parts[$index]) {
        $relative = [IO.Path]::GetRelativePath($Payload,$file.FullName).Replace('\','/')
        $directory = [IO.Path]::GetDirectoryName($relative).Replace('\','/')
        '<None Include="{0}" Pack="true" PackagePath="tools/remote-frame/{1}/{2}/" />' -f (Xml $file.FullName),$RuntimeIdentifier,(Xml $directory)
    }
    $items += '<None Include="{0}" Pack="true" PackagePath="buildTransitive/{1}.targets" />' -f (Xml "$PSScriptRoot/RemoteFrame.Office.Runtime.targets"),$partId
    $project = "$work/$partId.csproj"
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><EnableDefaultItems>false</EnableDefaultItems>
    <NoDefaultExcludes>true</NoDefaultExcludes><IncludeBuildOutput>false</IncludeBuildOutput>
    <PackageId>$partId</PackageId><Description>Complete Office runtime assets, part $($index + 1) of $($parts.Count). Install RemoteFrame.Office.Windows.</Description>
    <PackageLicenseExpression></PackageLicenseExpression><PackageLicenseFile>package-license.txt</PackageLicenseFile>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="package-license.txt" Pack="true" PackagePath="/" />
    $commonItems
    $($items -join "`n")
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $project
    Pack $project
    $dependencies.Add(('<PackageReference Include="{0}" Version="[{1}]" />' -f $partId,$Version))
}
$coordinator = "$work/$id.csproj"
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><EnableDefaultItems>false</EnableDefaultItems>
    <IncludeBuildOutput>false</IncludeBuildOutput><PackageId>$id</PackageId>
    <Description>Complete Office runtime, assembled from size-bounded dependency packages.</Description>
    <PackageLicenseExpression></PackageLicenseExpression><PackageLicenseFile>package-license.txt</PackageLicenseFile>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include="package-license.txt" Pack="true" PackagePath="/" />
    $commonItems
    $($dependencies -join "`n")
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $coordinator
Pack $coordinator
foreach ($package in Get-ChildItem $OutputDirectory -Filter "*.$Version.nupkg") {
    if ($package.Length -ge 240000000) { throw "Package exceeds the release size ceiling: $($package.Name)" }
}
Write-Output "Packed complete runtime in $($parts.Count) size-bounded parts plus $id."
