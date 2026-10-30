$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$test = Join-Path $root ('artifacts/split-package-test/' + [Guid]::NewGuid().ToString('N'))
$payload = "$test/payload"
$version = '0.0.0-split.g' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Force "$payload/office/program","$payload/worker","$payload/notices","$test/feed","$test/consumer" | Out-Null
foreach ($name in 'office/program/soffice.exe','worker/worker.exe','office/program/extensionless','office/program/.dir') {
    [IO.File]::WriteAllBytes("$payload/$name",[Text.Encoding]::UTF8.GetBytes(('TEST FIXTURE ' + $name).PadRight(1500000,'x')))
}
Set-Content "$payload/notices/LICENSE.txt" 'TEST FIXTURE LICENSE'
Set-Content "$payload/worker/LICENSE.txt" 'TEST FIXTURE WORKER LICENSE'
[IO.File]::WriteAllText("$payload/notices/NOTICE.txt",'TEST FIXTURE LARGE NOTICE'.PadRight(1100000,'x'))
& dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- --create $payload win-x64 fixture abc office/program/soffice.exe worker/worker.exe notices/LICENSE.txt notices/NOTICE.txt worker/LICENSE.txt
if ($LASTEXITCODE -ne 0) { throw 'Fixture manifest creation failed.' }
& "$root/packaging/Pack-SplitRuntime.ps1" -Payload $payload -Version $version -OutputDirectory "$test/feed" -PartBudgetBytes 2100000
$parts = @(Get-ChildItem "$test/feed" -Filter '*Part*.nupkg')
if ($parts.Count -lt 3) { throw 'Fixture did not exercise multiple dependency parts.' }
foreach ($package in Get-ChildItem "$test/feed" -Filter '*.nupkg') {
    $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $license = $zip.GetEntry('package-license.txt')
        if (-not $license -or $license.Length -ge 1000000) { throw 'NuGet license metadata exceeds its size limit.' }
        $notice = $zip.GetEntry('licenses/notices/NOTICE.txt')
        if (-not $notice -or $notice.Length -ne 1100000) { throw 'Full third-party notice was lost or truncated.' }
        $stream = $notice.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -ne (Get-FileHash "$payload/notices/NOTICE.txt").Hash) { throw 'Third-party notice content changed.' }
    } finally { $zip.Dispose() }
}
Write-Output 'PASS: License metadata stays below 1 MB while every package retains the full larger third-party notice.'
@"
<configuration><packageSources><clear/><add key="RemoteFrameLocal" value="$test/feed"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
"@ | Set-Content "$test/consumer/NuGet.Config"
@"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="RemoteFrame.Office.Runtime.win-x64" Version="[$version]" /></ItemGroup></Project>
"@ | Set-Content "$test/consumer/consumer.csproj"
foreach ($operation in 'build','publish') {
    $output = "$test/$operation"
    & dotnet $operation "$test/consumer/consumer.csproj" -c Release -o $output "-p:RestorePackagesPath=$test/cache"
    if ($LASTEXITCODE -ne 0) { throw "Clean consumer $operation failed." }
    $copied = "$output/remote-frame/win-x64"
    foreach ($file in Get-ChildItem $payload -Recurse -Force -File) {
        $relative = [IO.Path]::GetRelativePath($payload,$file.FullName)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath "$copied/$relative").Hash) {
            throw "Split package $operation lost or changed $relative"
        }
    }
    & dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- $copied win-x64
    if ($LASTEXITCODE -ne 0) { throw "Reassembled runtime failed validation after $operation." }
}
Write-Output 'PASS: A clean consumer restores all split dependencies and preserves every runtime byte during build and publish, including extensionless and hidden files.'

$components = "$test/components-feed"
& "$root/packaging/Pack-OfficeComponents.ps1" -Payload $payload -Version $version -OutputDirectory $components -PartBudgetBytes 2100000
@"
<configuration><packageSources><clear/><add key="RemoteFrameLocal" value="$components"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>
"@ | Set-Content "$test/consumer/NuGet.Config"
foreach ($mode in 'worker','combined') {
    $id = if ($mode -eq 'worker') { 'RemoteFrame.Office.Worker.Runtime.win-x64' } else { 'RemoteFrame.Office.Runtime.win-x64' }
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="$id" Version="[$version]" /></ItemGroup></Project>
"@ | Set-Content "$test/consumer/consumer.csproj"
    foreach ($operation in 'build','publish') {
        $output = "$test/$mode-$operation"
        & dotnet $operation "$test/consumer/consumer.csproj" -c Release -o $output "-p:RestorePackagesPath=$test/component-cache-$mode"
        if ($LASTEXITCODE -ne 0) { throw "Component consumer $mode $operation failed." }
        $deployed = "$output/remote-frame/win-x64"
        if ($mode -eq 'worker') {
            if (Test-Path "$deployed/components/libreoffice") { throw 'Worker-only deploy included LibreOffice.' }
            $assets = Get-Content "$test/consumer/obj/project.assets.json" -Raw
            if ($assets -match 'RemoteFrame.LibreOffice') { throw 'Worker-only restore included LibreOffice dependencies.' }
        }
        foreach ($file in Get-ChildItem $payload -Recurse -Force -File | Where-Object Name -NE 'runtime.json') {
            $relative = [IO.Path]::GetRelativePath($payload,$file.FullName).Replace('\','/')
            $component = if ($relative.StartsWith('worker/')) { 'worker' } else { 'libreoffice' }
            if ($mode -eq 'worker' -and $component -ne 'worker') { continue }
            if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash "$deployed/components/$component/$relative").Hash) { throw "Component lost or changed $relative" }
        }
        & dotnet run --project "$root/tools/RemoteFrame.RuntimeCheck" -c Release -- $deployed win-x64
        if ($LASTEXITCODE -ne 0) { throw 'Component deployment validation failed.' }
    }
}
Write-Output 'PASS: Worker-only restore/build/publish excludes LibreOffice; optional engine restores the complete byte-identical engine and independent manifests.'
