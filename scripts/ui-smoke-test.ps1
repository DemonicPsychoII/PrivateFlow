[CmdletBinding()]
param([string]$App = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\app\PrivateFlow.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$taskOldRoot = $env:PRIVATE_FLOW_DATA_DIR
$taskRoot = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-ui-' + [Guid]::NewGuid().ToString('N'))
$env:PRIVATE_FLOW_DATA_DIR = $taskRoot
$taskProcess = $null
function Find-Element($taskScope, $taskProperty, $taskValue) {
    $taskScope.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new($taskProperty, $taskValue))
}
function Find-ListItem($taskScope, $taskName) {
    $taskScope.FindFirst([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, $taskName),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)))
}
# The first element of a control type whose name starts with the prefix, such as a chip named with its current choice.
function Find-Prefixed($taskScope, $taskType, $taskPrefix) {
    foreach ($taskElement in $taskScope.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, $taskType))) {
        if ($taskElement.Current.Name.StartsWith($taskPrefix)) { return $taskElement }
    }
    return $null
}
function Get-Meetings($taskWindow) {
    $taskList = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Past meetings'
    @($taskList.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem)) | ForEach-Object { $_.Current.Name })
}
function Wait-Meetings($taskWindow, $taskCount) {
    for ($taskTry = 0; $taskTry -lt 20; $taskTry++) { $taskNames = Get-Meetings $taskWindow; if ($taskNames.Count -eq $taskCount) { return $taskNames }; Start-Sleep -Milliseconds 50 }
    throw "The History list showed $((Get-Meetings $taskWindow) -join ' | ') instead of $taskCount meetings."
}
# A finished meeting in the isolated archive, written before the app starts.
function New-Meeting($taskTitle, $taskDaysAgo, [string[]]$taskLines) {
    $taskId = [Guid]::NewGuid().ToString('N')
    $taskEnded = [DateTimeOffset]::UtcNow.AddDays(-$taskDaysAgo)
    $taskIndex = 0
    $taskSegments = @($taskLines | ForEach-Object { $taskStart = 10 * $taskIndex; $taskIndex++; [ordered]@{ Id = [Guid]::NewGuid().ToString('N'); Start = $taskStart; End = $taskStart + 6; Speaker = 'You'; Channel = 'mic'; Text = $_ } })
    $taskMeeting = [ordered]@{ Id = $taskId; Title = $taskTitle; Started = $taskEnded.AddMinutes(-20).ToString('o'); Ended = $taskEnded.ToString('o'); Status = 'Complete'; Chunks = @(); Segments = $taskSegments }
    $taskFolder = Join-Path (Join-Path $taskRoot 'meetings') $taskId
    New-Item -ItemType Directory -Path $taskFolder -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $taskFolder 'meeting.json'), ($taskMeeting | ConvertTo-Json -Depth 5))
}
function Wait-Title($taskWindow, $taskTitleId, $taskName) {
    for ($taskTry = 0; $taskTry -lt 20; $taskTry++) {
        $taskTitle = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::AutomationIdProperty) $taskTitleId
        if ($taskTitle -and $taskTitle.Current.Name -eq $taskName) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "$taskName did not open."
}
# Opens a page through the sidebar, or a section through the settings popup's list, and confirms the breadcrumb names it.
function Open-Page($taskWindow, $taskPage) {
    $taskList = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Pages'
    $taskItem = Find-ListItem $taskList $taskPage
    if (-not $taskItem) { throw "Navigation entry $taskPage was missing." }
    $taskItem.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Title $taskWindow 'PageTitle' $taskPage
}
function Open-Settings($taskWindow, $taskSection) {
    $taskList = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Settings sections'
    if (-not $taskList) {
        (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'SettingsButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        for ($taskTry = 0; $taskTry -lt 20 -and -not $taskList; $taskTry++) { Start-Sleep -Milliseconds 50; $taskList = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Settings sections' }
        if (-not $taskList) { throw 'The settings button did not open the settings popup.' }
    }
    $taskItem = Find-ListItem $taskList $taskSection
    if (-not $taskItem) { throw "Settings section $taskSection was missing." }
    $taskItem.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Title $taskWindow 'SettingsTitle' $taskSection
}
function Close-Settings($taskWindow) {
    (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Close settings').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($taskTry = 0; $taskTry -lt 20; $taskTry++) {
        if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Settings sections')) { return }
        Start-Sleep -Milliseconds 50
    }
    throw 'Close did not close the settings popup.'
}
try {
    New-Meeting 'Design review' 0 'Can we do the demo before lunch?','Lunch is at 12:30, so eleven would be safer.','The train leaves at twelve thirty.'
    New-Meeting 'Supplier call' 1 'Call me back at 1230 about the invoice.'
    New-Meeting 'Weekly sync' 2 'Nothing new this week.'
    $taskProcess = Start-Process -FilePath $App -ArgumentList '--diagnostic-ui' -WindowStyle Hidden -PassThru
    $taskWindow = $null
    for ($taskTry=0; $taskTry -lt 40; $taskTry++) {
        if ($taskProcess.HasExited) { throw "App exited unexpectedly ($($taskProcess.ExitCode))." }
        $taskCondition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$taskProcess.Id)
        $taskWindow = [Windows.Automation.AutomationElement]::RootElement.FindFirst([Windows.Automation.TreeScope]::Children,$taskCondition)
        if ($taskWindow) { break }
        Start-Sleep -Milliseconds 150
    }
    if (-not $taskWindow -or $taskWindow.Current.Name -ne 'Private Flow') { throw 'Main window did not initialize.' }
    foreach ($taskPage in 'Dictate','Meetings','History','Vocabulary') { Open-Page $taskWindow $taskPage }
    Write-Host 'PASS sidebar opens every page in the main window'
    foreach ($taskSection in 'Microphone and speakers','Shortcuts','Language and pasting','Text style','Profiles per app','Meeting defaults','Models','Speech recognition','About and updates') { Open-Settings $taskWindow $taskSection }
    Open-Settings $taskWindow 'Text style'
    if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Local polish wait for meeting notes')) { throw 'Text style has no wait for meeting notes.' }
    Close-Settings $taskWindow
    Write-Host 'PASS the settings button opens every settings section in the popup, and Close closes it'
    Write-Host 'PASS Text style offers the Local polish wait for meeting notes'
    Open-Page $taskWindow 'Meetings'
    foreach ($taskName in 'Start meeting','Meeting title','Meeting transcript') {
        if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) $taskName)) { throw "The Meetings page has no $taskName." }
    }
    foreach ($taskName in 'Meeting microphone: ','Meeting system audio: ','Meeting languages: English, German, Spanish','Meeting audio: ') {
        if (-not (Find-Prefixed $taskWindow ([Windows.Automation.ControlType]::Button) $taskName)) { throw "The Meetings page has no chip named $taskName with its choice." }
    }
    $taskLink = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Set a meeting shortcut'
    if (-not $taskLink) { throw 'The Meetings page has no link to set a meeting shortcut.' }
    $taskLink.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Wait-Title $taskWindow 'SettingsTitle' 'Shortcuts'
    Write-Host 'PASS the Meetings page has the record control, title, transcript and chips named with their choice, and its link opens the shortcut settings'
    Close-Settings $taskWindow
    Open-Page $taskWindow 'History'
    $taskNames = Wait-Meetings $taskWindow 3
    if (-not ($taskNames[0].StartsWith('Design review, Today '))) { throw "The History list did not start with the newest meeting: $($taskNames -join ' | ')." }
    if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Delete expired meetings')) { throw 'Delete expired meetings was not on the History page.' }
    (Find-Prefixed $taskWindow ([Windows.Automation.ControlType]::ListItem) 'Design review').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $taskNotesTab = Find-Prefixed $taskWindow ([Windows.Automation.ControlType]::RadioButton) 'Notes'
    if (-not $taskNotesTab -or -not (Find-Prefixed $taskWindow ([Windows.Automation.ControlType]::RadioButton) 'Transcript')) { throw 'The History page has no Transcript and Notes tabs.' }
    $taskNotesTab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $taskNotes = $null
    for ($taskTry = 0; $taskTry -lt 20 -and -not $taskNotes; $taskTry++) { Start-Sleep -Milliseconds 50; $taskNotes = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Meeting notes' }
    if (-not $taskNotes -or $taskNotes.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1).Trim().Length -eq 0) { throw 'The Notes tab showed no notes.' }
    if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Choose how notes are written')) { throw 'The notice with the rule-based notes has no link to the notes setting.' }
    if (Find-Prefixed $taskWindow ([Windows.Automation.ControlType]::Button) 'Write notes with') { throw 'A model button was offered although the rule-based notes are the default.' }
    Write-Host 'PASS the History page lists past meetings, and the Notes tab shows the rule-based notes with the link to the notes setting and no model button by default'
    $taskSearch = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Search meetings'
    $taskSearch.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('twelve thirty')
    $taskNames = Wait-Meetings $taskWindow 2
    if (-not ($taskNames[0].Contains(' 2 hits') -and $taskNames[1].Contains(' 1 hit'))) { throw "The search did not count the hits per meeting: $($taskNames -join ' | ')." }
    (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Search only the selected meeting').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    Wait-Meetings $taskWindow 1 | Out-Null
    $taskSearch.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Wait-Meetings $taskWindow 3 | Out-Null
    $taskSearch.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('twelve thirty')
    Wait-Meetings $taskWindow 2 | Out-Null
    $taskSearch.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('')
    Wait-Meetings $taskWindow 3 | Out-Null
    Write-Host 'PASS one search in the History header lists the meetings with hits and their counts, limits to the selected meeting, clears and does not keep the limit for the next search'
    Open-Settings $taskWindow 'Models'
    $taskRules = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Meeting notes: Rules on this PC'
    if (-not $taskRules -or -not $taskRules.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { throw 'The rule-based notes were not the default notes style.' }
    foreach ($taskName in 'Meeting notes: Local model','Meeting notes: API') { if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) $taskName)) { throw "The notes style $taskName was missing." } }
    foreach ($taskName in 'Install Whisper medium','Install Qwen3-4B (4-bit)','Install Qwen3.5-4B (4-bit)') { if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) $taskName)) { throw "Models did not offer $taskName." } }
    Write-Host 'PASS Models offers the three notes styles with the rule-based notes chosen by default, and the install of each model'
    Open-Settings $taskWindow 'Microphone and speakers'
    $taskOutput = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Playback device'
    $taskChosen = if ($taskOutput) { @($taskOutput.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()) } else { @() }
    if ($taskChosen.Count -ne 1 -or $taskChosen[0].Current.Name -ne 'Follow Windows default output') { throw 'The meeting output device was missing or did not follow the Windows default.' }
    if (-not (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Microphone')) { throw 'The microphone was not on the audio settings page.' }
    Write-Host 'PASS the microphone and the meeting output device, following the Windows default, share the audio settings page'
    Close-Settings $taskWindow
    $taskSearch = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Search pages and settings'
    $taskSearch.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('hold to talk')
    $taskFound = $null
    for ($taskTry = 0; $taskTry -lt 20 -and -not $taskFound; $taskTry++) { Start-Sleep -Milliseconds 50; $taskFound = Find-ListItem (Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Pages') 'Shortcuts' }
    if (-not $taskFound) { throw 'The sidebar search did not find the shortcut settings.' }
    $taskFound.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Wait-Title $taskWindow 'SettingsTitle' 'Shortcuts'
    Write-Host 'PASS a sidebar search for a setting opens the settings popup at its section'
    Open-Settings $taskWindow 'Language and pasting'
    $taskInsert = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'AutoInsert'
    if (-not $taskInsert -or $taskInsert.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { throw 'Automatic insertion was not enabled by default.' }
    Open-Settings $taskWindow 'Shortcuts'
    $taskShortcut = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Recording shortcut'
    if (-not $taskShortcut) { throw 'Adjustable shortcut field was missing.' }
    $taskMeetingShortcut = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'MeetingShortcutBox'
    if (-not $taskMeetingShortcut -or $taskMeetingShortcut.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne 'Not set') { throw 'Meeting shortcut field was missing or set by default.' }
    Write-Host 'PASS default insertion, adjustable shortcut controls and an unset meeting shortcut'
    $taskBehavior = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Shortcut behavior'
    if (-not $taskBehavior) { throw 'Shortcut behavior choice was missing.' }
    $taskBehavior.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $taskHold = Find-Element $taskBehavior ([Windows.Automation.AutomationElement]::NameProperty) 'Hold to talk'
    if (-not $taskHold) { throw 'Hold to talk option was missing.' }
    $taskHold.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $taskBehavior.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Open-Settings $taskWindow 'Language and pasting'
    $taskInsert = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'AutoInsert'
    $taskInsert.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    $taskSave = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::AutomationIdProperty) 'SavePreferencesButton'
    if (-not $taskSave) { throw 'Save preferences control was missing.' }
    $taskSave.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $taskConfigFile = Join-Path $taskRoot 'settings.json'
    for ($taskTry=0; $taskTry -lt 20 -and -not (Test-Path -LiteralPath $taskConfigFile); $taskTry++) { Start-Sleep -Milliseconds 100 }
    $taskConfig = Get-Content -LiteralPath $taskConfigFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($taskConfig.AutoInsert -ne $false -or $taskConfig.SettingsVersion -ne 5 -or $taskConfig.MeetingShortcutKey -ne 0) { throw 'Insertion opt-out did not save, or a meeting shortcut was saved without being chosen.' }
    if ($taskConfig.ShortcutBehavior -ne 'HoldToTalk') { throw 'Hold to talk did not save.' }
    if ($taskConfig.PlaybackDeviceId -ne '') { throw 'The meeting output device did not save as following the Windows default.' }
    Write-Host 'PASS isolated preference save without global shortcut registration'
    Write-Host 'PASS native WPF window initialized and controls were accessible'
    Open-Settings $taskWindow 'About and updates'
    $taskQuit = Find-Element $taskWindow ([Windows.Automation.AutomationElement]::NameProperty) 'Quit Private Flow'
    if (-not $taskQuit) { throw 'Quit control was missing.' }
    $taskQuit.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $taskProcess.WaitForExit(5000)) { throw 'Quit did not shut down the tray app.' }
    if ($taskProcess.ExitCode -ne 0) { throw "Unexpected exit code $($taskProcess.ExitCode)." }
    Write-Host 'PASS orderly Quit without recording audio'
} finally {
    if ($taskProcess -and -not $taskProcess.HasExited) { $taskProcess.Kill(); $taskProcess.WaitForExit() }
    $env:PRIVATE_FLOW_DATA_DIR = $taskOldRoot
    $taskResolvedRoot = [IO.Path]::GetFullPath($taskRoot)
    $taskExpectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $taskResolvedRoot.StartsWith($taskExpectedParent,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($taskResolvedRoot) -notlike 'PrivateFlow-ui-*') { throw 'Refusing cleanup outside the UI fixture directory.' }
    if (Test-Path -LiteralPath $taskResolvedRoot) { Remove-Item -LiteralPath $taskResolvedRoot -Recurse -Force }
}
