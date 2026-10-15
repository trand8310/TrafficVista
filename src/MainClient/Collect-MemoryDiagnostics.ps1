# Read-only Windows memory snapshot. No settings, processes or caches are changed.
# Default: console only. Save a report only by explicitly passing -OutputPath.
[CmdletBinding()]
param([string]$OutputPath, [switch]$AsJson)

$ErrorActionPreference = 'Stop'
$diagnosticWarnings = New-Object 'System.Collections.Generic.List[string]'
function Read-CimSnapshot([string]$ClassName) {
    try { Get-CimInstance -ClassName $ClassName -OperationTimeoutSec 15 }
    catch { $diagnosticWarnings.Add($ClassName + ': ' + $_.Exception.Message); return $null }
}
function To-MiB($Value) {
    if ($null -eq $Value) { return $null }
    return [Math]::Round([double]$Value / 1MB, 2)
}
function Counter-MiB($Source, [string]$Name) {
    if ($null -eq $Source -or $null -eq $Source.PSObject.Properties[$Name]) { return $null }
    return To-MiB $Source.$Name
}

$osSnapshot = Read-CimSnapshot 'Win32_OperatingSystem'
$computerSnapshot = Read-CimSnapshot 'Win32_ComputerSystem'
$memoryCounters = Read-CimSnapshot 'Win32_PerfFormattedData_PerfOS_Memory'
$processCounters = @(Read-CimSnapshot 'Win32_PerfFormattedData_PerfProc_Process')
$privateWorkingSets = @{}
foreach ($counter in $processCounters) {
    if ($null -ne $counter -and $counter.Name -ne '_Total' -and $counter.IDProcess -gt 0) {
        $privateWorkingSets[[int]$counter.IDProcess] = $counter.WorkingSetPrivate
    }
}

$processRows = @(foreach ($processItem in Get-Process) {
    try {
        [pscustomobject][ordered]@{
            Name = $processItem.ProcessName
            ProcessId = $processItem.Id
            SessionId = $processItem.SessionId
            WorkingSetMiB = To-MiB $processItem.WorkingSet64
            PrivateWorkingSetMiB = To-MiB $privateWorkingSets[$processItem.Id]
            PrivateCommitMiB = To-MiB $processItem.PrivateMemorySize64
            Handles = $processItem.HandleCount
        }
    }
    catch { $diagnosticWarnings.Add('Process exited or inaccessible: ' + $processItem.Id) }
    finally { $processItem.Dispose() }
})

$physicalTotal = $null; $physicalAvailable = $null; $physicalUsed = $null
if ($null -ne $osSnapshot) {
    $physicalTotal = To-MiB ([double]$osSnapshot.TotalVisibleMemorySize * 1KB)
    $physicalAvailable = To-MiB ([double]$osSnapshot.FreePhysicalMemory * 1KB)
    $physicalUsed = [Math]::Round($physicalTotal - $physicalAvailable, 2)
}
$report = [pscustomobject][ordered]@{
    CapturedAt = [DateTimeOffset]::Now.ToString('o')
    Machine = $env:COMPUTERNAME
    LogicalProcessors = $computerSnapshot.NumberOfLogicalProcessors
    OperatingSystem = $osSnapshot.Caption
    SystemMiB = [pscustomobject][ordered]@{
        PhysicalTotal = $physicalTotal
        PhysicalUsed = $physicalUsed
        PhysicalAvailable = $physicalAvailable
        Committed = Counter-MiB $memoryCounters 'CommittedBytes'
        CommitLimit = Counter-MiB $memoryCounters 'CommitLimit'
        NonpagedPool = Counter-MiB $memoryCounters 'PoolNonpagedBytes'
        PagedPool = Counter-MiB $memoryCounters 'PoolPagedBytes'
        PagedPoolResident = Counter-MiB $memoryCounters 'PoolPagedResidentBytes'
        SystemCacheResident = Counter-MiB $memoryCounters 'SystemCacheResidentBytes'
        ModifiedPageList = Counter-MiB $memoryCounters 'ModifiedPageListBytes'
        StandbyCore = Counter-MiB $memoryCounters 'StandbyCacheCoreBytes'
        StandbyNormal = Counter-MiB $memoryCounters 'StandbyCacheNormalPriorityBytes'
        StandbyReserve = Counter-MiB $memoryCounters 'StandbyCacheReserveBytes'
        FreeAndZero = Counter-MiB $memoryCounters 'FreeAndZeroPageListBytes'
        SystemDriverResident = Counter-MiB $memoryCounters 'SystemDriverResidentBytes'
        SystemCodeResident = Counter-MiB $memoryCounters 'SystemCodeResidentBytes'
    }
    ProcessSummary = [pscustomobject][ordered]@{
        Count = $processRows.Count
        SessionIds = @($processRows.SessionId | Sort-Object -Unique)
        SumPrivateWorkingSetMiB = [Math]::Round(($processRows | Measure-Object PrivateWorkingSetMiB -Sum).Sum, 2)
        SumPrivateCommitMiB = [Math]::Round(($processRows | Measure-Object PrivateCommitMiB -Sum).Sum, 2)
    }
    TopResidentProcesses = @($processRows | Sort-Object WorkingSetMiB -Descending | Select-Object -First 25)
    TopPrivateCommitProcesses = @($processRows | Sort-Object PrivateCommitMiB -Descending | Select-Object -First 25)
    AppBrowserDriverAndMemoryTools = @($processRows | Where-Object { $_.Name -match '^(MainClient|TrafficVista.*|chrome.*|node.*|MemReduct.*|Memory Compression|System)$' })
    Notes = @(
        'All sessions are queried. Protected/inaccessible process counters can be unavailable.'
        'PrivateCommit is committed virtual memory; it is NOT the amount currently resident in RAM.'
        'Working sets include shared pages. Do not sum working sets to account for physical RAM.'
        'Standby memory is reusable and included in available RAM; large standby alone does not explain high used RAM.'
        'Memory categories overlap and do not exhaust physical usage. Driver-locked/page-table/mapped-file attribution needs RAMMap.'
        'Null means unavailable, never zero. Values are sampled sequentially and can change during collection.'
    )
    Warnings = @($diagnosticWarnings.ToArray())
}
$jsonReport = $report | ConvertTo-Json -Depth 7
if ($OutputPath) {
    $absoluteReportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    if (Test-Path -LiteralPath $absoluteReportPath) { throw 'OutputPath already exists; choose a new filename.' }
    # CreateNew avoids replacing a report created concurrently by another collector.
    $reportStream = [System.IO.File]::Open($absoluteReportPath, [System.IO.FileMode]::CreateNew)
    $reportWriter = New-Object System.IO.StreamWriter($reportStream, (New-Object System.Text.UTF8Encoding($false)))
    try { $reportWriter.Write($jsonReport) }
    finally { $reportWriter.Dispose() }
}
if ($AsJson) { $jsonReport; return }
Write-Output ('Machine: ' + $report.Machine + '  CPU: ' + $report.LogicalProcessors + '  ' + $report.CapturedAt)
Write-Output 'SYSTEM MEMORY (MiB)'
$report.SystemMiB | Format-List | Out-String -Width 180 | Write-Output
Write-Output 'PROCESS SUMMARY (all sessions)'
$report.ProcessSummary | Format-List | Out-String -Width 180 | Write-Output
Write-Output 'APP / CHROME / NODE / MEMORY TOOLS'
$report.AppBrowserDriverAndMemoryTools | Format-Table -AutoSize | Out-String -Width 180 | Write-Output
Write-Output 'TOP RESIDENT PROCESSES'
$report.TopResidentProcesses | Format-Table -AutoSize | Out-String -Width 180 | Write-Output
Write-Output 'TOP PRIVATE COMMIT PROCESSES'
$report.TopPrivateCommitProcesses | Format-Table -AutoSize | Out-String -Width 180 | Write-Output
$report.Notes | Write-Output
$report.Warnings | Write-Output
if ($OutputPath) { Write-Output ('Report saved: ' + $absoluteReportPath) }
