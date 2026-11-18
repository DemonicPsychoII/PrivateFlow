# Removes a default installation of Private Flow from this PC: the program folder %LOCALAPPDATA%\Programs\PrivateFlow and
# the installer's leftover copies next to it, the data folder %LOCALAPPDATA%\PrivateFlow with everything in it (settings, its lock files,
# cloud reader token, models, runtimes, downloads, saved meetings with transcripts, notes and audio, and anything put
# there by hand), the Start menu shortcut, the autostart entry, and the installer's and updater's leftovers in %TEMP%.
# A relocated or custom installation is refused. It lists everything first and deletes only after the confirmation word,
# or with -Force. -WhatIf only lists.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    # Where install.ps1 puts them; the tests point them at throwaway places.
    [string]$StartMenuShortcut = (Join-Path ([Environment]::GetFolderPath('Programs')) 'Private Flow.lnk'),
    [string]$AutostartRegistryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    # Skips the confirmation word and ends a running Private Flow, for unattended use.
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# The module defines its aliases through ShouldProcess, so importing it under -WhatIf would print a "What if" line for each.
$whatIfRequested = $WhatIfPreference
$WhatIfPreference = $false
Import-Module CimCmdlets
$WhatIfPreference = $whatIfRequested

# The console can be gone by the time something is written (its host ended); output must never stop the script.
function Write-Line([string]$Text = '', [string]$Color) {
    try { if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text } }
    catch { try { [Console]::Out.WriteLine($Text) } catch { } }
}
function Test-Link([string]$Path) {
    try { [bool]([IO.File]::GetAttributes($Path) -band [IO.FileAttributes]::ReparsePoint) } catch { $false }
}
function Test-Inside([string]$Path, [string]$Folder) {
    ($Path.TrimEnd('\') + '\').StartsWith($Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Stop-Unsupported([string]$Reason) {
    Write-Line $Reason 'Red'
    Write-Line 'This script removes only a default installation in %LOCALAPPDATA%\Programs\PrivateFlow and %LOCALAPPDATA%\PrivateFlow. Nothing was removed.'
    Write-Line 'For a relocated or custom installation, quit Private Flow and delete by hand its program folder, its data folder, the Start menu shortcut "Private Flow" and the value PrivateFlow under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.'
    exit 1
}

$localAppData = "$env:LOCALAPPDATA".Trim()
if (-not $localAppData) { Stop-Unsupported 'LOCALAPPDATA is not set.' }
if ($localAppData -notmatch '^[A-Za-z]:\\' -or $localAppData.IndexOf(':', 2) -ge 0) { Stop-Unsupported "LOCALAPPDATA ($localAppData) is not a full path on a local drive." }
$localAppData = [IO.Path]::GetFullPath($localAppData).TrimEnd('\')
$programFolder = "$localAppData\Programs\PrivateFlow"
$dataFolder = "$localAppData\PrivateFlow"
$programFile = "$programFolder\PrivateFlow.exe"

# Why the default folders cannot be handled, or $null: a folder, or a folder on its path below the user profile, is a
# junction or symbolic link (a relocated installation), or the program folder is not Private Flow's.
function Get-FolderProblem {
    $profileFolder = $(if ($env:USERPROFILE) { [IO.Path]::GetFullPath($env:USERPROFILE).TrimEnd('\') } else { '' })
    foreach ($folder in $programFolder, $dataFolder) {
        $stop = $(if ($profileFolder -and (Test-Inside $folder $profileFolder)) { $profileFolder } else { [IO.Path]::GetPathRoot($folder).TrimEnd('\') })
        for ($current = $folder; $current.Length -gt $stop.Length; $current = Split-Path $current -Parent) {
            if (Test-Link $current) { return "$current is a junction or symbolic link, so this is a relocated installation." }
        }
    }
    # Remove-ProgramFolder deletes PrivateFlow.exe last, so an earlier run that could not remove the folder itself, such as one
    # another console had as its current folder, leaves it empty; anything else there without PrivateFlow.exe is not ours.
    if ((Test-Path -LiteralPath $programFolder) -and -not (Test-Path -LiteralPath $programFile -PathType Leaf) -and (Get-ChildItem -LiteralPath $programFolder -Force | Select-Object -First 1)) {
        return "$programFolder holds no PrivateFlow.exe."
    }
    $null
}
$problem = Get-FolderProblem
if ($problem) { Stop-Unsupported $problem }

# The program folder cannot be removed while this script runs from it: continue from a copy in %TEMP%.
if (Test-Inside $PSScriptRoot $programFolder) {
    $temporaryCopy = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-uninstall-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    Copy-Item -LiteralPath $PSCommandPath -Destination $temporaryCopy -WhatIf:$false
    Set-Location -LiteralPath ([IO.Path]::GetTempPath())
    try { & $temporaryCopy @PSBoundParameters } finally { Remove-Item -LiteralPath $temporaryCopy -Force -ErrorAction SilentlyContinue -WhatIf:$false }
    exit $LASTEXITCODE
}
foreach ($folder in $programFolder, $dataFolder) {
    if (Test-Inside (Get-Location).ProviderPath $folder) { Set-Location -LiteralPath ([IO.Path]::GetTempPath()) }
    if (Test-Inside ([Environment]::CurrentDirectory) $folder) { [Environment]::CurrentDirectory = [IO.Path]::GetTempPath() }
}

# Sizes never count what a link leads to.
function Measure-Tree([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $item -or (Test-Link $Path)) { return [long]0 }
    if (-not $item.PSIsContainer) { return [long]$item.Length }
    $total = [long]0
    foreach ($child in Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue) { $total += Measure-Tree $child.FullName }
    $total
}
function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return '{0:N1} GB' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:N1} MB' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:N0} KB' -f ($Bytes / 1KB) }
    "$Bytes bytes"
}
# The command a shortcut or Run value starts is this installation's PrivateFlow.exe: the path itself, quoted or not,
# followed by nothing or by arguments; a mention elsewhere in the command does not count.
function Test-StartsThisProgram([string]$Command) {
    $command = [Environment]::ExpandEnvironmentVariables($Command).Trim()
    foreach ($form in ('"' + $programFile + '"'), $programFile) {
        if ($command -ieq $form -or $command.StartsWith($form + ' ', [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    $false
}
function Get-ShortcutTarget { if (Test-Path -LiteralPath $StartMenuShortcut -PathType Leaf) { (New-Object -ComObject WScript.Shell).CreateShortcut($StartMenuShortcut).TargetPath } }
function Get-AutostartCommand { (Get-ItemProperty -LiteralPath $AutostartRegistryKey -Name PrivateFlow -ErrorAction SilentlyContinue).PrivateFlow }

# install.ps1 stages a new copy and keeps the previous one next to the program folder; a failed swap can leave them.
$leftoverCopies = @(Get-ChildItem -LiteralPath "$localAppData\Programs" -Directory -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '^PrivateFlow\.(?:new|old)-[0-9a-f]{8}$' -and -not (Test-Link $_.FullName) -and (Test-Path -LiteralPath "$($_.FullName)\PrivateFlow.exe" -PathType Leaf) } | ForEach-Object { $_.FullName })
# install.ps1 -FromCloud downloads into PrivateFlow-install-<id>; the in-app update runs a copy PrivateFlow-update-<id>.ps1.
$temporaryFolder = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$temporaryLeftovers = @(Get-ChildItem -LiteralPath $temporaryFolder -Force -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -match '^PrivateFlow-install-[0-9a-f]{32}$' -or $_.Name -match '^PrivateFlow-update-[0-9a-f]{32}\.ps1$' } | ForEach-Object { $_.FullName })
$shortcutTarget = Get-ShortcutTarget
$removeShortcut = $shortcutTarget -and (Test-StartsThisProgram ('"' + $shortcutTarget + '"'))
$autostartCommand = Get-AutostartCommand
$removeAutostart = $autostartCommand -and (Test-StartsThisProgram $autostartCommand)

# Private Flow and its helpers: processes whose executable is inside the default folders (the app, its speech worker,
# whisper-cli.exe, llama-server.exe), and the PowerShell, curl and gh processes they start, found as their descendants.
# Never by name or command line; never this script or the processes that started it. Console hosts are never ended: they
# close with the programs they serve, and ending one ends every program attached to it.
$processFolders = @($programFolder, $dataFolder) + $leftoverCopies
function Get-PrivateFlowProcesses {
    $allProcesses = @(Get-CimInstance Win32_Process)
    $processesById = @{}
    foreach ($process in $allProcesses) { $processesById[[int]$process.ProcessId] = $process }
    $spared = @{}
    for ($processId = $PID; $processesById.ContainsKey($processId) -and -not $spared.ContainsKey($processId); $processId = [int]$processesById[$processId].ParentProcessId) { $spared[$processId] = $true }
    $selected = @{}
    foreach ($process in $allProcesses) {
        if (-not $spared.ContainsKey([int]$process.ProcessId) -and $process.ExecutablePath -and @($processFolders | Where-Object { Test-Inside $process.ExecutablePath $_ }).Count -gt 0) { $selected[[int]$process.ProcessId] = $process }
    }
    do {
        $added = $false
        foreach ($process in $allProcesses) {
            $parent = $selected[[int]$process.ParentProcessId]
            # A parent id can belong to an older, ended process; a real child starts after its parent.
            if ($parent -and -not $selected.ContainsKey([int]$process.ProcessId) -and -not $spared.ContainsKey([int]$process.ProcessId) -and
                $process.Name -in 'powershell.exe', 'pwsh.exe', 'curl.exe', 'gh.exe' -and $process.CreationDate -ge $parent.CreationDate) { $selected[[int]$process.ProcessId] = $process; $added = $true }
        }
    } while ($added)
    @($selected.Values | Sort-Object ProcessId)
}
$runningProcesses = @(Get-PrivateFlowProcesses)

Write-Line 'Private Flow uninstall' 'Cyan'
Write-Line
Write-Line 'Removes:'
foreach ($folder in @($programFolder) + $leftoverCopies + @($dataFolder) + $temporaryLeftovers) {
    if (Test-Path -LiteralPath $folder) { Write-Line "  $folder  $(Format-Size (Measure-Tree $folder))" }
}
if (Test-Path -LiteralPath $dataFolder) {
    $meetings = @(Get-ChildItem -LiteralPath "$dataFolder\meetings" -Directory -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^[0-9a-f]{32}$' })
    Write-Line "      Saved meetings: $($meetings.Count), with their transcripts, notes and audio"
    foreach ($model in Get-ChildItem -LiteralPath "$dataFolder\models" -File -Force -ErrorAction SilentlyContinue) { Write-Line "      Model: $($model.Name)  $(Format-Size $model.Length)" }
}
if ($removeShortcut) { Write-Line "  Start menu shortcut $StartMenuShortcut" }
elseif ($shortcutTarget) { Write-Line "  Left in place: $StartMenuShortcut (starts '$shortcutTarget', not this installation)" }
if ($removeAutostart) { Write-Line "  Autostart value PrivateFlow in $AutostartRegistryKey" }
elseif ($autostartCommand) { Write-Line "  Left in place: autostart value PrivateFlow in $AutostartRegistryKey (starts '$autostartCommand', not this installation)" }
if ($env:PRIVATE_FLOW_DATA_DIR) { Write-Line "  Left in place: $($env:PRIVATE_FLOW_DATA_DIR) (PRIVATE_FLOW_DATA_DIR); this script does not handle it, delete it by hand if you no longer need it" }
if (-not ((Test-Path -LiteralPath $programFolder) -or (Test-Path -LiteralPath $dataFolder) -or $leftoverCopies -or $temporaryLeftovers -or $removeShortcut -or $removeAutostart)) {
    Write-Line 'Nothing of Private Flow was found. Nothing was removed.'
    exit 0
}
Write-Line
Write-Line 'Everything in these folders is deleted, also files you put there yourself. Saved meetings, their transcripts, notes and audio, the settings and the downloaded models cannot be recovered.' 'Yellow'
if ($runningProcesses.Count -gt 0) {
    Write-Line 'Private Flow is running:' 'Yellow'
    foreach ($process in $runningProcesses) { Write-Line "  $($process.Name) (process $($process.ProcessId))" }
    Write-Line 'Quit it from its tray icon (Quit) and run this script again, or confirm below to end these processes now.'
}

if (-not $PSCmdlet.ShouldProcess('Private Flow on this PC (the locations listed above)', 'Remove')) { exit 0 }
if (-not $Force) {
    Write-Line "Type UNINSTALL and press Enter to remove everything listed above$(if ($runningProcesses.Count -gt 0) { ', end the processes above' }). Anything else cancels."
    try { $answer = Read-Host 'Confirmation' } catch { $answer = '' }
    if ("$answer".Trim() -ine 'UNINSTALL') { Write-Line 'Cancelled. Nothing was removed.'; exit 1 }
}

# The prompt may have waited for minutes: the folders are checked again, and processes listed again; each is ended only
# while its id still belongs to the process listed (same start time).
$problem = Get-FolderProblem
if ($problem) { Write-Line "Stopped: $problem The folders changed since they were listed. Nothing was removed." 'Red'; exit 1 }
for ($attempt = 0; $attempt -lt 3 -and ($runningProcesses = @(Get-PrivateFlowProcesses)).Count -gt 0; $attempt++) {
    foreach ($listed in $runningProcesses) {
        try {
            $live = [Diagnostics.Process]::GetProcessById([int]$listed.ProcessId)
            if ([Math]::Abs(($live.StartTime - $listed.CreationDate).TotalSeconds) -lt 1) { $live.Kill(); [void]$live.WaitForExit(15000) }
        } catch { }
    }
}
if ($runningProcesses.Count -gt 0) {
    Write-Line 'These processes did not end, so nothing was removed:' 'Red'
    foreach ($process in $runningProcesses) { Write-Line "  $($process.Name) (process $($process.ProcessId))" }
    exit 1
}

$failures = New-Object System.Collections.Generic.List[string]
$script:stopReason = $null
# Deletes a file, a link (as a link, never what it leads to) or a folder with everything in it, looking at each level
# when the walk reaches it. Before each deletion the folders above it, up to the root being removed, are checked again
# not to have become links; a folder is removed only once empty.
function Remove-Tree([string]$Path, [string]$Root) {
    if ($script:stopReason) { return $false }
    for ($current = Split-Path $Path -Parent; Test-Inside $current $Root; $current = Split-Path $current -Parent) {
        if (Test-Link $current) { $script:stopReason = "$current became a link during the run"; return $false }
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $item) { return $true }
    try {
        if (Test-Link $Path) {
            if ($item.PSIsContainer) { [IO.Directory]::Delete($Path, $false) } else { [IO.File]::Delete($Path) }
            return $true
        }
        if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) { $item.Attributes = $item.Attributes -bxor [IO.FileAttributes]::ReadOnly }
        if (-not $item.PSIsContainer) { [IO.File]::Delete($Path); return $true }
        $everythingRemoved = $true
        foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) { if (-not (Remove-Tree $child.FullName $Root)) { $everythingRemoved = $false } }
        if ($everythingRemoved) { [IO.Directory]::Delete($Path, $false) }
        $everythingRemoved
    } catch { $failures.Add("$Path`: $($_.Exception.GetBaseException().Message.Trim())"); $false }
}
# PrivateFlow.exe goes last and only once the rest is gone, so a rerun after a locked file still finds the program folder.
function Remove-ProgramFolder([string]$Folder) {
    $everythingRemoved = $true
    foreach ($child in @(Get-ChildItem -LiteralPath $Folder -Force | Where-Object { $_.Name -ne 'PrivateFlow.exe' })) { if (-not (Remove-Tree $child.FullName $Folder)) { $everythingRemoved = $false } }
    $everythingRemoved -and (Remove-Tree $Folder $Folder)
}

$removed = New-Object System.Collections.Generic.List[string]
try {
    if ($removeShortcut) {
        if (-not (Test-StartsThisProgram ('"' + (Get-ShortcutTarget) + '"'))) { $script:stopReason = "the Start menu shortcut changed since it was listed and now starts '$(Get-ShortcutTarget)'" }
        elseif (Remove-Tree $StartMenuShortcut $StartMenuShortcut) { $removed.Add($StartMenuShortcut) }
    }
    if ($removeAutostart -and -not $script:stopReason) {
        if (-not (Test-StartsThisProgram "$(Get-AutostartCommand)")) { $script:stopReason = "the autostart value changed since it was listed and now starts '$(Get-AutostartCommand)'" }
        else { Remove-ItemProperty -LiteralPath $AutostartRegistryKey -Name PrivateFlow; $removed.Add("Autostart value PrivateFlow in $AutostartRegistryKey") }
    }
    foreach ($leftover in $temporaryLeftovers) { if (Remove-Tree $leftover $temporaryFolder) { $removed.Add($leftover) } }
    foreach ($folder in $leftoverCopies + @($programFolder, $dataFolder)) {
        if ($script:stopReason -or -not (Test-Path -LiteralPath $folder)) { continue }
        if ($problem = Get-FolderProblem) { $script:stopReason = $problem; break }
        $folderRemoved = $(if ($folder -eq $dataFolder) { Remove-Tree $folder $folder } else { Remove-ProgramFolder $folder })
        if ($folderRemoved) { $removed.Add($folder) }
    }
} catch { $script:stopReason = "an unexpected error: $($_.Exception.Message)" }

Write-Line
Write-Line 'Removed:'
foreach ($line in $removed) { Write-Line "  $line" }
if ($script:stopReason) { Write-Line "Stopped: $($script:stopReason). Nothing more was removed after that." 'Red' }
if ($failures.Count -gt 0) {
    Write-Line 'Could not be deleted (close the program using them, or restart Windows, and run this script again):' 'Red'
    foreach ($failure in $failures) { Write-Line "  $failure" }
}
if ($script:stopReason -or $failures.Count -gt 0) { exit 1 }
Write-Line 'Private Flow is uninstalled.' 'Green'
exit 0
