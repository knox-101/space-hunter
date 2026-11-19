<#
  Clean-Temp.ps1 - silently empties %TEMP%, C:\Windows\Temp, C:\Windows\Prefetch
  and clears the Win+R (Run dialog) history.

  Files that are in use are skipped silently (no dialog ever appears).
  Files changed in the last -MinAgeMinutes are left alone, so tools that are
  running right now (Claude Code, other agents, installers) don't lose work files.

  Usage:
    .\Clean-Temp.ps1                    # normal run
    .\Clean-Temp.ps1 -DryRun            # only report what would be deleted
    .\Clean-Temp.ps1 -MinAgeMinutes 0   # delete everything that isn't locked
#>
param(
    [switch]$DryRun,
    [int]$MinAgeMinutes = 60
)

$ErrorActionPreference = 'SilentlyContinue'

$logDir  = Join-Path $env:LOCALAPPDATA 'TempCleaner'
$logFile = Join-Path $logDir 'log.txt'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$cutoff  = (Get-Date).AddMinutes(-$MinAgeMinutes)
$stats   = @{ Deleted = 0; Skipped = 0; Bytes = [int64]0; DirsRemoved = 0 }

$targets = @(
    $env:TEMP,
    (Join-Path $env:SystemRoot 'Temp'),
    (Join-Path $env:SystemRoot 'Prefetch')
) | Where-Object { $_ } | Select-Object -Unique

function Clear-Folder([string]$dir, [bool]$isRoot) {
    $entries = Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue
    foreach ($e in $entries) {
        # Never follow junctions/symlinks out of the temp folder
        if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            if (-not $DryRun) { try { $e.Delete(); $script:stats.DirsRemoved++ } catch { $script:stats.Skipped++ } }
            continue
        }
        if ($e.PSIsContainer) {
            Clear-Folder $e.FullName $false
        }
        elseif ($e.LastWriteTime -gt $cutoff) {
            $script:stats.Skipped++
        }
        else {
            $size = $e.Length
            if ($DryRun) { $script:stats.Deleted++; $script:stats.Bytes += $size; continue }
            try {
                if ($e.IsReadOnly) { $e.IsReadOnly = $false }
                $e.Delete()                      # throws if the file is in use -> skip
                $script:stats.Deleted++; $script:stats.Bytes += $size
            } catch { $script:stats.Skipped++ }
        }
    }
    # Remove the folder itself if it's now empty (never the root target)
    if (-not $isRoot -and -not $DryRun) {
        if (-not (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) {
            try { [IO.Directory]::Delete($dir); $script:stats.DirsRemoved++ } catch {}
        }
    }
}

$notes = @()
foreach ($t in $targets) {
    # Safety: only touch real folders that look like temp/prefetch, never a drive root
    if (-not (Test-Path -LiteralPath $t -PathType Container)) { continue }
    $full = (Resolve-Path -LiteralPath $t).Path.TrimEnd('\')
    if ($full.Length -le 3 -or $full -notmatch '\\(Temp|Prefetch)$') { $notes += "Refused unsafe target: $full"; continue }
    if (-not (Get-ChildItem -LiteralPath $full -Force -ErrorAction SilentlyContinue | Select-Object -First 1) -and $full -match 'Prefetch$') {
        $notes += "Prefetch not readable (needs admin) - skipped"
    }
    Clear-Folder $full $true
}

# Win+R Run history
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\RunMRU'
if (Test-Path $runKey) {
    $names = (Get-Item $runKey).Property
    if ($names -and -not $DryRun) { Remove-ItemProperty -Path $runKey -Name $names -Force }
    $notes += "Run history entries $(if ($DryRun) {'found'} else {'cleared'}): $(@($names).Count)"
}

$mb   = [math]::Round($stats.Bytes / 1MB, 1)
$mode = if ($DryRun) { 'DRY RUN' } else { 'CLEANED' }
$line = "{0}  {1}  files={2}  freed={3} MB  skipped(in use/recent)={4}  folders={5}  {6}" -f `
        (Get-Date -Format 'yyyy-MM-dd HH:mm'), $mode, $stats.Deleted, $mb, $stats.Skipped, $stats.DirsRemoved, ($notes -join '; ')
Add-Content -Path $logFile -Value $line
Write-Output $line
