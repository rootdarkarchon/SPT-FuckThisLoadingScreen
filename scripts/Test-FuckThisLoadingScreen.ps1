param(
    [string]$SptInstallPath = $env:SPT_INSTALL_PATH,
    [string]$Unity = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $SptInstallPath -or -not (Test-Path -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))) { throw 'Pass -SptInstallPath or set SPT_INSTALL_PATH to an EFT 40743 / SPT 4.1 installation.' }
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'src\FuckThisLoadingScreen'
& dotnet build (Join-Path $source 'FuckThisLoadingScreen.csproj') -c Release "-p:SptInstallPath=$SptInstallPath" --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'FTLS build failed' }
& dotnet build (Join-Path $repo 'tools\FTLS.LevelStageTimer\FTLS.LevelStageTimer.csproj') -c Release "-p:SptInstallPath=$SptInstallPath" --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Timer build failed for companion compatibility fixture' }
$project = Join-Path $repo 'obj\FuckThisLoadingScreen\UnityFixture'
foreach ($folder in @('Assets\Editor', 'Packages', 'ProjectSettings')) { New-Item -ItemType Directory -Force (Join-Path $project $folder) | Out-Null }
foreach ($name in @('LoadingPolicy.cs', 'PolicyHooks.cs', 'SpawnZoneHooks.cs', 'SceneBundleHooks.cs', 'LoadingFrameCap.cs', 'ExperimentReports.cs', 'BoundedScenes.cs', 'Tests\BoundedSceneValidation.cs', 'Tests\Validation.cs')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $project 'Assets\Editor') -Force
}
Copy-Item -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Comfort.Unity.dll') -Destination (Join-Path $project 'Assets\Editor') -Force
Copy-Item -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Comfort.dll') -Destination (Join-Path $project 'Assets\Editor') -Force
foreach ($name in @('0Harmony.dll','Mono.Cecil.dll','Mono.Cecil.Mdb.dll','Mono.Cecil.Pdb.dll','Mono.Cecil.Rocks.dll','MonoMod.Utils.dll','MonoMod.RuntimeDetour.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "BepInEx\core\$name") -Destination (Join-Path $project 'Assets\Editor') -Force
}
Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Value '{"dependencies":{}}'
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Value 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'install.txt') -Value $SptInstallPath
$log = Join-Path $project 'validation.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $Unity -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','FTLSValidation.Run','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
while (-not $process.WaitForExit(1000)) {
    if (([DateTime]::UtcNow - $started).TotalMinutes -gt 4) { $process.Kill(); throw "Unity loading validation timed out: $log" }
}
$process.Refresh()
$result = Join-Path $project 'result.txt'
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result) -or (Get-Item -LiteralPath $result).LastWriteTimeUtc -lt $started) {
    if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result }
    Select-String -LiteralPath $log -Pattern 'error CS|Exception|FAIL:' | Select-Object -Last 12
    throw "FTLS validation failed ($($process.ExitCode)): $log"
}
Get-Content -LiteralPath $result
