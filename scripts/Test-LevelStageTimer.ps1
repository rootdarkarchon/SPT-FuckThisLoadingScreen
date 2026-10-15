param(
    [string]$SptInstallPath = $env:SPT_INSTALL_PATH,
    [string]$Unity = 'C:\Program Files\Unity 2022.3.43f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
if (-not $SptInstallPath -or -not (Test-Path -LiteralPath (Join-Path $SptInstallPath 'EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll'))) { throw 'Pass -SptInstallPath or set SPT_INSTALL_PATH to an EFT 40743 / SPT 4.1 installation.' }
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo 'tools\FTLS.LevelStageTimer'
& dotnet build (Join-Path $source 'FTLS.LevelStageTimer.csproj') -c Release "-p:SptInstallPath=$SptInstallPath" --nologo --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Stage timer build failed' }
$project = Join-Path $repo 'obj\FTLS.LevelStageTimer\UnityFixture'
foreach ($folder in @('Assets\Editor', 'Packages', 'ProjectSettings')) {
    New-Item -ItemType Directory -Force (Join-Path $project $folder) | Out-Null
}
foreach ($name in @('Recorder.cs', 'LoadingHooks.cs', 'HookCatalog.cs', 'DetailProbes.cs', 'SceneProbes.cs', 'SceneActivation.cs', 'RetentionProbes.cs', 'Tests\RetentionValidation.cs', 'Tests\Program.cs', 'Tests\SceneValidation.cs', 'Tests\ActivationValidation.cs', 'Tests\UnityValidation.cs')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $project 'Assets\Editor') -Force
}
foreach ($name in @('0Harmony.dll','Mono.Cecil.dll','Mono.Cecil.Mdb.dll','Mono.Cecil.Pdb.dll','Mono.Cecil.Rocks.dll','MonoMod.Utils.dll','MonoMod.RuntimeDetour.dll')) {
    Copy-Item -LiteralPath (Join-Path $SptInstallPath "BepInEx\core\$name") -Destination (Join-Path $project 'Assets\Editor') -Force
}
Set-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Value '{"dependencies":{}}'
Set-Content -LiteralPath (Join-Path $project 'ProjectSettings\ProjectVersion.txt') -Value 'm_EditorVersion: 2022.3.43f1'
Set-Content -LiteralPath (Join-Path $project 'install.txt') -Value $SptInstallPath
$log = Join-Path $project 'validation.log'
$started = [DateTime]::UtcNow
$process = Start-Process -FilePath $Unity -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','LoadingTimerValidation.Run','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru
while (-not $process.WaitForExit(1000)) {
    if (([DateTime]::UtcNow - $started).TotalMinutes -gt 4) { $process.Kill(); throw "Unity validation timed out: $log" }
}
$process.Refresh()
$result = Join-Path $project 'result.txt'
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $result) -or (Get-Item -LiteralPath $result).LastWriteTimeUtc -lt $started) {
    Get-Content -LiteralPath $log -Tail 45
    throw "Loading timer validation failed ($($process.ExitCode)): $log"
}
Get-Content -LiteralPath $result
