param(
    [string]$ToolsRoot = (Join-Path $PSScriptRoot '../../artifacts/build-tools'),
    [string]$BashPath = 'C:/cygwin64/bin/bash.exe'
)
# Dot-source this file before invoking Build-LibreOffice.ps1.
$ErrorActionPreference = 'Stop'
$ToolsRoot = [IO.Path]::GetFullPath($ToolsRoot)
$cygpath = Join-Path (Split-Path $BashPath -Parent) 'cygpath.exe'
if (-not (Test-Path -LiteralPath $BashPath)) { throw 'Install Cygwin first, or set BashPath.' }
& $BashPath --login -c 'exec /bin/bash -c "command -v patch && command -v nasm"'
if ($LASTEXITCODE -ne 0) { throw 'Install the Cygwin patch and nasm packages before building.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$profile = Join-Path $PSScriptRoot 'profiles/win-x64.args'
$year = (Select-String -LiteralPath $profile -Pattern '^--with-visual-studio=(2022|2026)$').Matches.Groups[1].Value
$major = if ($year -eq '2026') { 18 } else { 17 }
$vsPath = & $vswhere -latest -products '*' -version "[$major,$($major + 1))" -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "Visual Studio $year C++ tools were not found" }
$clang = @('VC/Tools/Llvm/x64/bin/clang-cl.exe','VC/Tools/Llvm/bin/clang-cl.exe') | ForEach-Object { Join-Path $vsPath $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $clang) { throw 'Install Visual Studio C++ Clang Compiler and its LLVM toolset component.' }
foreach ($tool in @(
    @{Name='pkgconf-2.4.3.exe';Hash='791cd6dbc56f7268fbf9c4652d6634b0f5c59687ab4e504565e58245952edd41'},
    @{Name='make.exe';Hash='146d6f2b0ea57647b11b506a95048a7be73232e1feeeccbc1013651f992423d8'}
)) {
    $path = Join-Path $ToolsRoot "opt/bin/$($tool.Name)"
    if (-not (Test-Path -LiteralPath $path)) { throw "Stage the pinned build tool at $path; see the source-build guide." }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $tool.Hash) { throw "Build tool checksum mismatch: $path" }
}
$jomArchive = Join-Path $ToolsRoot 'jom_1_1_4.zip'
$jom = Join-Path $ToolsRoot 'jom/jom.exe'
if (-not (Test-Path -LiteralPath $jomArchive) -or -not (Test-Path -LiteralPath $jom)) { throw 'Stage the pinned JOM archive and extract it to artifacts/build-tools/jom.' }
if ((Get-FileHash -LiteralPath $jomArchive -Algorithm SHA256).Hash -ne 'd533c1ef49214229681e90196ed2094691e8c4a0a0bef0b2c901debcb562682b') { throw 'JOM archive checksum mismatch.' }
$archive = [IO.Compression.ZipFile]::OpenRead($jomArchive)
try {
    $entry = $archive.GetEntry('jom.exe')
    if (-not $entry) { throw 'Pinned JOM archive does not contain jom.exe.' }
    $entryStream = $entry.Open()
    try { $jomHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)) }
    finally { $entryStream.Dispose() }
} finally { $archive.Dispose() }
if ((Get-FileHash -LiteralPath $jom -Algorithm SHA256).Hash -ne $jomHash) { throw 'JOM executable differs from its verified archive.' }
$env:JOM = & $cygpath -au $jom
$linkWrapper = Join-Path $ToolsRoot 'link-wrapper/RemoteFrame.LinkWrapper.exe'
if (-not (Test-Path -LiteralPath $linkWrapper)) { throw 'Publish tools/RemoteFrame.LinkWrapper to artifacts/build-tools/link-wrapper before building.' }
$toolVersion = Get-ChildItem -LiteralPath "$vsPath/VC/Tools/MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
$env:REMOTEFRAME_REAL_LINK = Join-Path $vsPath "VC/Tools/MSVC/$toolVersion/bin/Hostx64/x64/link.exe"
if (-not (Test-Path -LiteralPath $env:REMOTEFRAME_REAL_LINK)) { throw 'The native MSVC linker was not found.' }
$env:GMAKE_OPTIONS = '-rs gb_LINK=' + $linkWrapper.Replace('\','/') + ' MAKEOVERRIDES='
$toolsUnix = & $cygpath -au $ToolsRoot
if ($LASTEXITCODE -ne 0 -or $toolsUnix -notmatch '^/[A-Za-z0-9/_.-]+$') { throw 'Use a simple, space-free build tools path.' }
$nativeMake = (Join-Path $ToolsRoot 'opt/bin/make.exe').Replace('\','/')
$envFile = Join-Path $ToolsRoot 'native-build-environment.sh'
$environmentText = 'export PATH="' + $toolsUnix + '/opt/bin:/usr/bin:/bin:$PATH"' + "`n" + 'export GNUMAKE="' + $nativeMake + '"' + "`n"
[IO.File]::WriteAllText($envFile, $environmentText, [Text.UTF8Encoding]::new($false))
$env:BASH_ENV = $envFile.Replace('\','/')
$env:LODE_HOME = $toolsUnix
$clangShort = & $cygpath -d $clang
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the Clang compiler path.' }
$env:LO_CLANG_CC = $clangShort.Replace('\','/') + ' --target=x86_64-pc-windows-msvc -m64'
$env:LO_CLANG_CXX = $env:LO_CLANG_CC
$env:ForceImportBeforeCppTargets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/PythonUwpCompat.props'))
$env:LO_CLANG_SHOWINCLUDES_PREFIX = 'Note: including file:'
Write-Output "Configured verified Windows build tools for Visual Studio $year in this PowerShell session."
