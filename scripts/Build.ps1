<#
.SYNOPSIS
Builds, packages and optionally installs this mod.
.DESCRIPTION
Identity (name, version, GUIDs, which parts exist) is read from Directory.Build.props through MSBuild.
Builds the solution, stages the payload under artifacts/package, zips it to
artifacts/release/<Name>-<version>-<SptLabel>-<treehash12>.zip, verifies the exact entry list and file
hashes, and writes <zip>.receipt.json. Nothing is installed unless -Install is given.
Client payload: BepInEx/plugins/<Name>/ and, when src/<Name>.Patcher exists, BepInEx/patchers/<Name>/. Never .pdb.
Server payload: SPT_Runtime/user/mods/<Name>/ with config.defaults.json. config.json is user-owned and never shipped;
the mod creates it from the defaults on first start.
-Install refuses while EscapeFromTarkov (or the SPT server of the target install, for server mods)
is running, overwrites the payload files in place (no backup, nothing deleted), and writes
artifacts/install-receipt.json.
.PARAMETER Configuration
Build configuration. Default Release.
.PARAMETER SptInstallPath
SPT install root. Default: $env:TARKOV_SPT_INSTALL.
.PARAMETER Install
Copy the packaged payload into the SPT install.
.PARAMETER SkipBuild
Package the existing build output without running dotnet build.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$SptInstallPath = $env:TARKOV_SPT_INSTALL,
    [switch]$Install,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot

function Get-Sha256Hex([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

function Get-WorkingTreeHash([string]$RepositoryRoot) {
    # Content hash over tracked and untracked, non-ignored files; build outputs are ignored.
    $fileList = (& git -C $RepositoryRoot ls-files -z --cached --others --exclude-standard) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the repository working tree for package naming.' }
    $paths = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $fileList.Split([char]0)) {
        if ($path.Length -gt 0) { [void]$paths.Add($path) }
    }
    $manifest = [Text.StringBuilder]::new()
    foreach ($path in $paths) {
        $fullPath = Join-Path $RepositoryRoot $path
        # Deleted tracked files are absent from the working tree.
        if (-not (Test-Path -LiteralPath $fullPath)) { continue }
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Cannot hash working-tree entry: $path" }
        $fileHash = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash
        [void]$manifest.Append($path).Append([char]0).Append($fileHash).Append([char]0)
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifest.ToString()))
        return ([BitConverter]::ToString($digest)).Replace('-', '').ToLowerInvariant().Substring(0, 12)
    } finally { $sha256.Dispose() }
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

# --- install path -----------------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($SptInstallPath)) { throw 'SPT install path is empty. Set TARKOV_SPT_INSTALL or pass -SptInstallPath.' }
if (-not (Test-Path -LiteralPath $SptInstallPath -PathType Container)) { throw "SPT install path does not exist: $SptInstallPath" }
$SptInstallPath = [IO.Path]::GetFullPath($SptInstallPath).TrimEnd('\', '/')
foreach ($required in 'BepInEx\core\BepInEx.dll', 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $SptInstallPath $required) -PathType Leaf)) { throw "SPT install '$SptInstallPath' lacks $required." }
}

# --- git ----------------------------------------------------------------------------------------------
$null = & git --version
if ($LASTEXITCODE -ne 0) { throw 'git is required.' }
$top = (& git -C $root rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($top)) { throw "$root is not a git repository (run git init)." }
if ([IO.Path]::GetFullPath(([string]$top).Trim()).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($root).TrimEnd('\', '/')) {
    throw "The git repository root ($top) is not the project root ($root); the package hash would cover the wrong tree."
}

# --- identity from MSBuild ------------------------------------------------------------------------------
$slnx = @(Get-ChildItem -LiteralPath $root -Filter '*.slnx' -File)
if ($slnx.Count -ne 1) { throw "Expected exactly one .slnx in $root, found $($slnx.Count)." }
$probe = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter '*.csproj' -File | Select-Object -First 1)
if ($probe.Count -eq 0) { throw 'No project found under src/.' }
$propertyJson = & dotnet msbuild $probe[0].FullName -nologo -getProperty:ModName -getProperty:Version -getProperty:SptLabel -getProperty:PluginGuid -getProperty:ServerModGuid -getProperty:HasClient -getProperty:HasServer "-p:SptInstallPath=$SptInstallPath"
if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate Directory.Build.props through MSBuild.' }
$props = (($propertyJson -join "`n") | ConvertFrom-Json).Properties
foreach ($property in 'ModName', 'Version', 'SptLabel', 'PluginGuid') {
    if ([string]::IsNullOrWhiteSpace($props.$property)) { throw "MSBuild property $property is empty." }
}
$name = $props.ModName
$version = $props.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw "Version '$version' is not semver (1.2.3 or 1.2.3-beta.1)." }
$hasClient = $props.HasClient -eq 'true'
$hasServer = $props.HasServer -eq 'true'
if (-not $hasClient -and -not $hasServer) { throw 'Directory.Build.props sets neither HasClient nor HasServer.' }
if ($hasServer -and -not (Test-Path -LiteralPath (Join-Path $SptInstallPath 'SPT_Runtime\SPTarkov.Server.Core.dll') -PathType Leaf)) {
    throw "SPT install '$SptInstallPath' lacks SPT_Runtime\SPTarkov.Server.Core.dll."
}

$clientDll = Join-Path $root "src\$name\bin\$Configuration\netstandard2.1\$name.dll"
$serverDll = Join-Path $root "src\$name.Server\bin\$Configuration\net10.0\$name.Server.dll"
$serverConfig = Join-Path $root "src\$name.Server\config.defaults.json"
$patcherProject = Join-Path $root "src\$name.Patcher"
$license = Join-Path $root 'LICENSE'
if (-not (Test-Path -LiteralPath $license -PathType Leaf)) { throw 'LICENSE is missing.' }

# --- build ----------------------------------------------------------------------------------------------
$treeHash = Get-WorkingTreeHash $root
$gitHead = (& git -C $root rev-parse --verify -q HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $gitHead = $null } else { $gitHead = ([string]$gitHead).Trim() }

if (-not $SkipBuild) {
    & dotnet build $slnx[0].FullName -c $Configuration "-p:SptInstallPath=$SptInstallPath" --nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }
}
if ((Get-WorkingTreeHash $root) -ne $treeHash) { throw 'Working tree changed during the build; run again.' }

# --- payload: zip entry -> source file -----------------------------------------------------------------
$payload = [ordered]@{}
if ($hasClient) {
    $payload["BepInEx/plugins/$name/$name.dll"] = $clientDll
    $payload["BepInEx/plugins/$name/LICENSE"] = $license
    # Third-party dependency copied next to the plugin dll (CopyLocalLockFileAssemblies) plus its license.
    $payload["BepInEx/plugins/$name/AssetsTools.NET.dll"] = Join-Path (Split-Path -Parent $clientDll) 'AssetsTools.NET.dll'
    $payload["BepInEx/plugins/$name/AssetsTools.NET-LICENSE.txt"] = Join-Path $root "src\$name\AssetsTools.NET-LICENSE.txt"
    # Optional patcher: src/<Name>.Patcher builds <Name>.Patcher.dll, shipped to BepInEx/patchers/<Name>/.
    if (Test-Path -LiteralPath $patcherProject -PathType Container) {
        $patcherDlls = @(Get-ChildItem -Path (Join-Path $patcherProject "bin\$Configuration\*\$name.Patcher.dll") -File -ErrorAction SilentlyContinue)
        if ($patcherDlls.Count -ne 1) { throw "Expected exactly one built $name.Patcher.dll under $patcherProject\bin\$Configuration, found $($patcherDlls.Count)." }
        $payload["BepInEx/patchers/$name/$name.Patcher.dll"] = $patcherDlls[0].FullName
    }
}
if ($hasServer) {
    $payload["SPT_Runtime/user/mods/$name/$name.Server.dll"] = $serverDll
    $payload["SPT_Runtime/user/mods/$name/config.defaults.json"] = $serverConfig
    $payload["SPT_Runtime/user/mods/$name/LICENSE"] = $license
}
if ($payload.Keys | Where-Object { $_ -like 'SPT_Runtime/user/mods/*/config.json' }) { throw 'config.json is user-owned and must not be shipped; ship config.defaults.json.' }
foreach ($entry in $payload.Keys) {
    if ($entry -like '*.pdb') { throw "Release payloads never ship .pdb files: $entry" }
    if (-not (Test-Path -LiteralPath $payload[$entry] -PathType Leaf)) { throw "Missing payload source for ${entry}: $($payload[$entry])" }
}

$artifacts = Join-Path $root 'artifacts'
$packageRoot = Join-Path $artifacts 'package'
$releaseRoot = Join-Path $artifacts 'release'
if (Test-Path -LiteralPath $packageRoot) { Remove-Item -LiteralPath $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot, $releaseRoot -Force | Out-Null
$expectedHashes = [ordered]@{}
foreach ($entry in $payload.Keys) {
    $staged = Join-Path $packageRoot ($entry.Replace('/', '\'))
    New-Item -ItemType Directory -Path (Split-Path -Parent $staged) -Force | Out-Null
    Copy-Item -LiteralPath $payload[$entry] -Destination $staged
    $expectedHashes[$entry] = Get-Sha256Hex $payload[$entry]
    if ((Get-Sha256Hex $staged) -ne $expectedHashes[$entry]) { throw "Staged copy of $entry differs from its source." }
}

# --- zip ------------------------------------------------------------------------------------------------
$zip = Join-Path $releaseRoot "$name-$version-$($props.SptLabel)-$treeHash.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip }
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $payload.Keys) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $packageRoot ($entry.Replace('/', '\'))), $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose(); $stream.Dispose() }

# --- verify the zip: exact entry list and content hashes --------------------------------------------------
$expectedNames = @($payload.Keys | Sort-Object { $_ } -CaseSensitive)
$read = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $actualNames = @($read.Entries | ForEach-Object { $_.FullName } | Sort-Object { $_ } -CaseSensitive)
    if (($actualNames -join "`n") -ne ($expectedNames -join "`n")) {
        throw "Zip entries differ from the expected list.`nExpected:`n$($expectedNames -join "`n")`nActual:`n$($actualNames -join "`n")"
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($zipEntry in $read.Entries) {
            $entryStream = $zipEntry.Open()
            try { $actual = ([BitConverter]::ToString($sha.ComputeHash($entryStream))).Replace('-', '') } finally { $entryStream.Dispose() }
            if ($actual -ne $expectedHashes[$zipEntry.FullName]) { throw "Zip entry $($zipEntry.FullName) differs from its source (SHA-256)." }
        }
    } finally { $sha.Dispose() }
} finally { $read.Dispose() }
if ((Get-WorkingTreeHash $root) -ne $treeHash) { throw 'Working tree changed while packaging; run again.' }

# --- receipt --------------------------------------------------------------------------------------------
$zipSha = Get-Sha256Hex $zip
$entryRecords = @(foreach ($entry in $payload.Keys) {
    [ordered]@{ path = $entry; sha256 = $expectedHashes[$entry]; bytes = (Get-Item -LiteralPath $payload[$entry]).Length }
})
$receipt = [ordered]@{
    name = $name
    version = $version
    sptLabel = $props.SptLabel
    configuration = $Configuration
    gitHead = $gitHead
    treeHash = $treeHash
    zipSha256 = $zipSha
    entries = $entryRecords
}
Write-Utf8 "$zip.receipt.json" ($receipt | ConvertTo-Json -Depth 5)
Write-Output "Package: $zip"
Write-Output "Receipt: $zip.receipt.json"

# --- install (explicit only) ----------------------------------------------------------------------------
if ($Install) {
    if (Get-Process -Name 'EscapeFromTarkov' -ErrorAction SilentlyContinue) { throw 'EscapeFromTarkov is running; close the game before installing.' }
    if ($hasServer) {
        foreach ($server in @(Get-Process -Name 'SPT.Server' -ErrorAction SilentlyContinue)) {
            $serverPath = $null
            try { $serverPath = $server.Path } catch { $serverPath = $null }
            if ([string]::IsNullOrWhiteSpace($serverPath)) { throw "An SPT.Server process (id $($server.Id)) is running and its path cannot be read; stop it before installing." }
            if ($serverPath.StartsWith($SptInstallPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "SPT.Server from $SptInstallPath is running (id $($server.Id)); stop it before installing." }
        }
    }

    $installed = @()
    foreach ($entry in $payload.Keys) {
        $destination = Join-Path $SptInstallPath ($entry.Replace('/', '\'))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $packageRoot ($entry.Replace('/', '\'))) -Destination $destination -Force
        if ((Get-Sha256Hex $destination) -ne $expectedHashes[$entry]) { throw "Installed file differs from the package: $destination" }
        $installed += $entry
    }
    $installReceipt = [ordered]@{
        name = $name
        version = $version
        utc = [DateTime]::UtcNow.ToString('o')
        sptInstallPath = $SptInstallPath
        zip = (Split-Path -Leaf $zip)
        zipSha256 = $zipSha
        treeHash = $treeHash
        installed = $installed
    }
    Write-Utf8 (Join-Path $artifacts 'install-receipt.json') ($installReceipt | ConvertTo-Json -Depth 5)
    Write-Output "Installed $($installed.Count) file(s) into $SptInstallPath"
}
