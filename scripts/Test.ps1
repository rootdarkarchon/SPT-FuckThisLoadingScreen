<#
.SYNOPSIS
Runs the Unity batch validation harness for FuckThisLoadingScreen.
.DESCRIPTION
Builds the plugin, assembles a throw-away Unity 2022.3.43f1 project under artifacts/test-output/unity-fixture that
compiles the plugin sources (everything except the BepInEx-bound Plugin, Settings, ModPaths and
ConfigurationManagerAttributes, which are validated through the built plugin DLL) together with tests/FuckThisLoadingScreen.Validation,
and runs FTLSValidation.Run in batch mode against the installed game assemblies. Prints the result file (PASS: N ...).
Nothing is installed into the game.
.PARAMETER SptInstallPath
SPT install root. Default: $env:TARKOV_SPT_INSTALL.
.PARAMETER Unity
Path of Unity.exe (2022.3.43f1). Default: Unity 2022.3.43f1 under Program Files.
.PARAMETER SkipBuild
Reuse the existing Release build of the plugin.
#>
[CmdletBinding()]
param(
    [string]$SptInstallPath = $env:TARKOV_SPT_INSTALL,
    [string]$Unity = (Join-Path $env:ProgramFiles 'Unity 2022.3.43f1\Editor\Unity.exe'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
if (-not $SptInstallPath -or -not (Test-Path -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))) { throw 'Set TARKOV_SPT_INSTALL or pass -SptInstallPath (EFT 40743 / SPT 4.1 installation).' }
if (-not (Test-Path -LiteralPath $Unity -PathType Leaf)) { throw "Unity editor not found: $Unity (pass -Unity)." }
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'src\FuckThisLoadingScreen'
$tests = Join-Path $repo 'tests\FuckThisLoadingScreen.Validation'
if (-not $SkipBuild) {
    & dotnet build (Join-Path $repo 'FuckThisLoadingScreen.slnx') -c Release "-p:SptInstallPath=$SptInstallPath" --nologo --disable-build-servers
    if ($LASTEXITCODE -ne 0) { throw 'FTLS build failed' }
}
$built = Join-Path $source 'bin\Release\netstandard2.1'
foreach ($name in 'FuckThisLoadingScreen.dll', 'AssetsTools.NET.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $built $name))) { throw "Missing build output: $built\$name" }
}

$project = Join-Path $repo 'artifacts\test-output\unity-fixture'
$editor = Join-Path $project 'Assets\Editor'
# Rebuilt on every run so removed sources cannot linger; the Library cache stays for faster imports.
if (Test-Path -LiteralPath $editor) { Remove-Item -LiteralPath $editor -Recurse -Force }
foreach ($folder in @('Assets\Editor', 'Packages', 'ProjectSettings', 'plugin')) { New-Item -ItemType Directory -Force (Join-Path $project $folder) | Out-Null }

# Plugin sources compile into the Unity project. These four need BepInEx at runtime and are exercised through the built DLL instead.
$bepInExBound = @('Plugin.cs', 'Settings.cs', 'ModPaths.cs', 'ConfigurationManagerAttributes.cs')
Get-ChildItem -LiteralPath $source -Filter '*.cs' -File | Where-Object { $bepInExBound -notcontains $_.Name } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $editor -Force }
Get-ChildItem -LiteralPath $tests -Filter '*.cs' -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $editor -Force }

Copy-Item -LiteralPath (Join-Path $built 'AssetsTools.NET.dll') -Destination $editor -Force
foreach ($name in 'FuckThisLoadingScreen.dll', 'AssetsTools.NET.dll') { Copy-Item -LiteralPath (Join-Path $built $name) -Destination (Join-Path $project 'plugin') -Force }
foreach ($name in @('Comfort.Unity.dll', 'Comfort.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "EscapeFromTarkov_Data\Managed\$name") -Destination $editor -Force
}
foreach ($name in @('0Harmony.dll', 'Mono.Cecil.dll', 'Mono.Cecil.Mdb.dll', 'Mono.Cecil.Pdb.dll', 'Mono.Cecil.Rocks.dll', 'MonoMod.Utils.dll', 'MonoMod.RuntimeDetour.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "BepInEx\core\$name") -Destination $editor -Force
}
Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Value '{"dependencies":{}}'
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Value 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'install.txt') -Value $SptInstallPath

$log = Join-Path $project 'validation.log'
$result = Join-Path $project 'result.txt'
if (Test-Path -LiteralPath $result) { Remove-Item -LiteralPath $result }
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $Unity -ArgumentList @('-batchmode', '-nographics', '-quit', '-projectPath', ('"' + $project + '"'), '-executeMethod', 'FTLSValidation.Run', '-logFile', ('"' + $log + '"')) -WindowStyle Hidden -PassThru
while (-not $process.WaitForExit(1000)) {
    if (([DateTime]::UtcNow - $started).TotalMinutes -gt 10) { $process.Kill(); throw "Unity loading validation timed out: $log" }
}
$process.Refresh()
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result) -or (Get-Item -LiteralPath $result).LastWriteTimeUtc -lt $started) {
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    Select-String -LiteralPath $log -Pattern 'error CS|Exception|FAIL:' | Select-Object -Last 12
    throw "FTLS validation failed ($($process.ExitCode)): $log"
}
Get-Content -LiteralPath $result
