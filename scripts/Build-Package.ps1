param([string]$SptInstallPath = $env:SPT_INSTALL_PATH)
$ErrorActionPreference = 'Stop'
if (-not $SptInstallPath -or -not (Test-Path -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))) { throw 'Pass -SptInstallPath or set SPT_INSTALL_PATH to an EFT 40743 / SPT 4.1 installation.' }
$repo = Split-Path $PSScriptRoot -Parent
& dotnet build (Join-Path $repo 'src\FuckThisLoadingScreen\FuckThisLoadingScreen.csproj') -c Release "-p:SptInstallPath=$SptInstallPath" --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'FTLS build failed' }
$dll = Join-Path $repo 'obj\FuckThisLoadingScreen\plugin\netstandard2.1\FuckThisLoadingScreen.dll'
$semver = ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion.Split('.')[0..2] -join '.')
$zip = Join-Path $repo "bin\Release\FuckThisLoadingScreen-$semver-SPT4.1.zip"
New-Item -ItemType Directory -Path (Split-Path $zip) -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$file = [IO.File]::Open($zip, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create)
$entryName = 'BepInEx/plugins/FuckThisLoadingScreen/FuckThisLoadingScreen.dll'
$licenseEntryName = $entryName.Substring(0, $entryName.LastIndexOf('/')) + '/LICENSE'
try {
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $dll, $entryName) | Out-Null
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $repo 'LICENSE'), $licenseEntryName) | Out-Null
}
finally { $archive.Dispose(); $file.Dispose() }
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    if ($archive.Entries.Count -ne 2 -or $archive.Entries[0].FullName -ne $entryName -or $archive.Entries[1].FullName -ne $licenseEntryName) { throw 'Unexpected FTLS payload' }
    $stream = $archive.Entries[0].Open(); $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
    if ($hash -ne (Get-FileHash -LiteralPath $dll).Hash) { throw 'FTLS package hash mismatch' }
} finally { $archive.Dispose() }
Get-FileHash -LiteralPath $zip
