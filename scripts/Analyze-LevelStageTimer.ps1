param([Parameter(Mandatory=$true)][string]$CapturePath)
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
function Get-UnionMilliseconds($Rows) {
    $intervals = @($Rows | ForEach-Object {
        $start = [double]::Parse($_.start_ms, $culture)
        [pscustomobject]@{ Start = $start; End = $start + [double]::Parse($_.duration_ms, $culture) }
    } | Sort-Object Start, End)
    if ($intervals.Count -eq 0) { return 0.0 }
    $start = $intervals[0].Start; $end = $intervals[0].End; $total = 0.0
    foreach ($interval in $intervals | Select-Object -Skip 1) {
        if ($interval.Start -le $end) { $end = [Math]::Max($end, $interval.End) }
        else { $total += $end - $start; $start = $interval.Start; $end = $interval.End }
    }
    return [Math]::Round($total + $end - $start, 3)
}
$folders = if (Test-Path -LiteralPath (Join-Path $CapturePath 'stages.csv')) { @(Get-Item -LiteralPath $CapturePath) }
else { @(Get-ChildItem -LiteralPath $CapturePath -Directory | Sort-Object Name | Select-Object -Last 10) }
foreach ($folder in $folders) {
    $metadata = Get-Content -LiteralPath (Join-Path $folder.FullName 'capture.txt')
    Write-Output "Capture: $($folder.FullName)"
    $metadata | Where-Object { $_ -match '^(Outcome|Elapsed ms|Dropped spans|Dropped samples|Per-stage suppressed spans|Dropped aggregate stages|Dropped scenes|Scene observer errors|Scene requests|Recorded scenes|Bundle error read failures|Unrecorded lifecycle detail calls|Unmatched lifecycle calls|Ambiguous lifecycle calls|Lifecycle observer errors|Lifecycle depth overflows|Pinewood observed calls|Pinewood retained slow calls|Scene handler methods|Unrecorded scene handler calls|Out-of-scope scene handler calls):' } | Write-Output
    $rows = @(Import-Csv -LiteralPath (Join-Path $folder.FullName 'stages.csv'))
    $completed = @($rows | Where-Object outcome -EQ 'completed')
    Write-Output "Recorded wait coverage ms: $(Get-UnionMilliseconds @($completed | Where-Object kind -EQ 'wait'))"
    Write-Output 'Coverage is inclusive wall time, merged within each stage. Different stages overlap; do not sum. Waits may overlap useful work.'
    $completed | Group-Object stage | ForEach-Object {
        [pscustomobject]@{
            Stage = $_.Name
            Kind = $_.Group[0].kind
            Calls = $_.Count
            Coverage_ms = Get-UnionMilliseconds $_.Group
            Max_call_ms = [Math]::Round(($_.Group | ForEach-Object { [double]::Parse($_.duration_ms, $culture) } | Measure-Object -Maximum).Maximum, 3)
        }
    } | Sort-Object Coverage_ms -Descending | Format-Table -AutoSize
    $aggregatePath = Join-Path $folder.FullName 'aggregates.csv'
    if (Test-Path -LiteralPath $aggregatePath) {
        Write-Output 'All-call aggregates (including calls beyond detail caps; summed inclusive durations, not merged coverage):'
        Import-Csv -LiteralPath $aggregatePath | Sort-Object { [double]::Parse($_.total_call_ms, $culture) } -Descending |
            Format-Table stage,calls,completed,threw,incomplete_calls,total_call_ms,max_call_ms -AutoSize
    }
    $scenePath = Join-Path $folder.FullName 'scene-phases.csv'
    $retentionPath = Join-Path $folder.FullName 'retention.csv'
    if (Test-Path -LiteralPath $retentionPath) {
        Write-Output 'Dependency retention (inclusive wall time; membership and actual-yield columns are blank when unmeasured):'
        Import-Csv -LiteralPath $retentionPath | Format-Table id,start_ms,elapsed_ms,roots_started,recursive_entries,membership_checks,admissions_per_root_sum,repeated_visits,actual_yields,threshold_at_entry,outcome -AutoSize
    }
    if (Test-Path -LiteralPath $scenePath) {
        Write-Output 'Scene phases (wall time; combined includes held activation waits; dispatch is a subset of Unity time):'
        Write-Output 'Automatic-unsplit means readiness was not observed; blank activation time is unknown, not zero. Bundle wait includes dependencies.'
        $sceneRows = @(Import-Csv -LiteralPath $scenePath)
        if ($sceneRows.Count -gt 0 -and $sceneRows[0].PSObject.Properties['ready_to_activation_ms']) {
            Write-Output 'Largest explicit readiness holds (overlapping waits, not additive CPU time):'
            $sceneRows | Where-Object ready_to_activation_ms -NE '' | Sort-Object { [double]::Parse($_.ready_to_activation_ms, $culture) } -Descending |
                Select-Object -First 10 | Format-Table scene_id,scene,ready_to_activation_ms,completion_to_native_return_ms -AutoSize
        }
        $sceneRows | Group-Object bundle_outcome | ForEach-Object {
            [pscustomobject]@{ Bundle_outcome = $_.Name; Requests = $_.Count; Elapsed_ms = [Math]::Round(($_.Group | ForEach-Object { [double]::Parse($_.bundle_ms, $culture) } | Measure-Object -Sum).Sum, 3) }
        } | Format-Table -AutoSize
        $sceneRows | Sort-Object { if ($_.unity_combined_ms) { [double]::Parse($_.unity_combined_ms, $culture) } else { -1 } } -Descending |
            Format-Table scene_id,scene,initial_activation,outcome,bundle_ms,unity_to_ready_ms,activation_ms,unity_combined_ms,dispatch_ms,activation_kind -AutoSize
        $failedBundles = @($sceneRows | Where-Object { $_.bundle_outcome -eq 'failed' -and $_.bundle_error })
        if ($failedBundles.Count -gt 0) {
            Write-Output 'Largest failed scene bundle requests and their recorded reasons (up to1024 characters per reason):'
            $failedBundles | Sort-Object { [double]::Parse($_.bundle_ms, $culture) } -Descending | Select-Object -First 10 |
                Format-List scene_id,scene,bundle_ms,bundle_error
        }
    }
    $samplePath = Join-Path $folder.FullName 'samples.csv'
    if (Test-Path -LiteralPath $samplePath) {
        $samples = @(Import-Csv -LiteralPath $samplePath)
        if ($samples.Count -gt 0 -and $samples[0].PSObject.Properties['target_frame_rate']) {
            Write-Output 'Observed frame pacing settings (job scheduler ticks are measured, not overridden):'
            $samples | Group-Object target_frame_rate,v_sync_count,scheduler_frame_ticks | Select-Object Name,Count | Format-Table -AutoSize
        }
    }
    $lifecyclePath = Join-Path $folder.FullName 'scene-lifecycle.csv'
    $lifecycleTotalsPath = Join-Path $folder.FullName 'scene-lifecycle-totals.csv'
    if (Test-Path -LiteralPath $lifecycleTotalsPath) {
        Write-Output 'Largest per-scene lifecycle self totals (independent of method-detail cap; entry windows kept separate):'
        Import-Csv -LiteralPath $lifecycleTotalsPath | Where-Object { $_.entry_window -notin @('after-completion','scene-loaded-dispatch','scene-loaded-handler') } |
            Sort-Object { [double]::Parse($_.self_ms, $culture) } -Descending | Select-Object -First 20 |
            Format-Table scene_id,scene,entry_window,calls,incomplete_calls,inclusive_ms,self_ms,max_ms,crossed_completion -AutoSize
    }
    if (Test-Path -LiteralPath $lifecyclePath) {
        Write-Output 'Largest managed lifecycle self times during loading/activation (relative to observed nesting; includes native callees and instrumentation):'
        Write-Output 'Entry window is observed at callback entry. Crossed-completion calls can extend outside activation. After-completion Start is separate; sceneLoaded dispatch overlaps its observed child callbacks.'
        Import-Csv -LiteralPath $lifecyclePath | Where-Object entry_window -NE 'after-completion' |
            Sort-Object { [double]::Parse($_.self_ms, $culture) } -Descending | Select-Object -First 20 |
            Format-Table scene_id,entry_window,method,calls,inclusive_ms,self_ms,max_ms,crossed_completion -AutoSize
    }
    $pinewoodPath = Join-Path $folder.FullName 'pinewood-slow-calls.csv'
    $sceneHandlersPath = Join-Path $folder.FullName 'scene-handlers.csv'
    if (Test-Path -LiteralPath $sceneHandlersPath) {
        Write-Output 'Scene-loaded subscriber totals by method (inclusive overlaps dispatcher; self excludes observed child calls):'
        Import-Csv -LiteralPath $sceneHandlersPath | Group-Object method | ForEach-Object {
            [pscustomobject]@{
                Method = $_.Name
                Calls = ($_.Group | Measure-Object calls -Sum).Sum
                Incomplete = ($_.Group | Measure-Object incomplete_calls -Sum).Sum
                Inclusive_ms = [Math]::Round(($_.Group | ForEach-Object { [double]::Parse($_.inclusive_ms, $culture) } | Measure-Object -Sum).Sum, 3)
                Self_ms = [Math]::Round(($_.Group | ForEach-Object { [double]::Parse($_.self_ms, $culture) } | Measure-Object -Sum).Sum, 3)
            }
        } | Sort-Object Self_ms -Descending | Format-Table -AutoSize
    }
    if (Test-Path -LiteralPath $pinewoodPath) {
        Write-Output 'Slowest Pinewood indoor callback invocations (retained top256; inclusive durations overlap):'
        Import-Csv -LiteralPath $pinewoodPath | Select-Object -First 15 |
            Format-Table entry_window,method,start_ms,duration_ms,self_ms,depth,outcome,crossed_completion -AutoSize
    }
    $handlers = @($completed | Where-Object { $_.stage -like 'callback.*.handler' -and $_.detail })
    if ($handlers.Count -gt 0) {
        Write-Output 'Top callback subscribers (inclusive recorded coverage; capped stages can be partial):'
        $handlers | Group-Object stage, detail | ForEach-Object {
            [pscustomobject]@{
                Stage = $_.Group[0].stage
                Method = $_.Group[0].detail
                Calls = $_.Count
                Coverage_ms = Get-UnionMilliseconds $_.Group
                Max_call_ms = [Math]::Round(($_.Group | ForEach-Object { [double]::Parse($_.duration_ms, $culture) } | Measure-Object -Maximum).Maximum, 3)
            }
        } | Sort-Object Coverage_ms -Descending | Select-Object -First 15 | Format-Table -AutoSize
    }
    if (@($rows | Where-Object outcome -NE 'completed').Count -gt 0) {
        Write-Output 'Incomplete/failed/disposed spans are excluded from the table; inspect stages.csv for their status.'
    }
}
