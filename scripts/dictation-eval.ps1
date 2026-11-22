# Measures dictation through the app's own path: the recording is transcribed in the live 20 s windows, the language is
# resolved as after a recording, and every output style runs on the same transcript. Concise talks to a loopback echo stub.
[CmdletBinding()]
param(
    [string]$App = '',
    # <auto|fixed>[-no-detection]: auto detects each recording's language, fixed selects the language it is spoken in.
    [string[]]$Configuration = @('auto', 'auto-no-detection', 'fixed'),
    # Real FLEURS utterances per language; 0 leaves real speech out.
    [int]$RealSpeech = 30,
    # Writes the recordings as an audio library for the test build (--library-audio) into this folder outside the repository, then stops.
    [string]$LibraryOutput = '',
    # en, de or es measures only the recordings in that language; empty measures all.
    [ValidateSet('', 'en', 'de', 'es')][string]$Language = '',
    # Downloads the assets and synthesizes the clips, then stops (scripts/verify.ps1 does this once before its jobs).
    [switch]$PrepareOnly,
    [string]$EnginePath = '', [string]$ModelPath = '', [string]$VadModelPath = '', [string]$EngineLabel = '', [string]$MeetingModelPath = ''
)
. (Join-Path $PSScriptRoot 'eval-common.ps1')
$App = Get-VerificationHost $App
# powershell.exe -File passes a list as one comma-separated string.
$Configuration = @($Configuration | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($taskEntry in $Configuration) { if ($taskEntry -notmatch '^(auto|fixed)(-no-detection)?$') { throw "Unknown configuration $taskEntry." } }
$taskEngine = Resolve-Engine $EnginePath $ModelPath $VadModelPath $EngineLabel $MeetingModelPath
$taskRoot = Join-Path $EvalOutput 'dictation-eval'
$taskData = Join-Path $taskRoot 'data'; $taskReports = Join-Path $taskRoot 'reports'
foreach ($taskFolder in $taskData, $taskReports) { New-Item -ItemType Directory -Force -Path $taskFolder | Out-Null }
$taskVoices = Get-VoiceTable

$taskCases = New-Object System.Collections.Generic.List[object]
$taskTurn = 0
# Spoken is what the voice says; Reference marks the words as spoken, Edited what Clean should paste when it differs.
function Add-Case([string]$name, [string]$language, [string]$spoken, [hashtable]$options = @{}) {
    $speaker = $(if ($options.Speaker) { $options.Speaker } else { @('Alice', 'Ben', 'Carl')[$script:taskTurn++ % 3] })
    $voice = $taskVoices["$speaker|$language"]
    if (-not $voice) { return }
    $clip = $(if ($options.Clip) { $options.Clip } else { Get-Clip $voice $spoken })
    $case = [ordered]@{ Name = "$language-$name"; Language = $language; Clip = $clip; Source = 'synthetic'; Reference = $spoken; Edited = $null
        SpokenFormatting = $false; LiteralMode = $false; TechnicalMode = $false; Vocabulary = ''; Corrections = @(); Snippets = @(); Checks = @(); Tags = @($speaker) }
    foreach ($key in $options.Keys) { if ($key -ne 'Speaker') { $case[$key] = $options[$key] } }
    $taskCases.Add($case)
}
function Check([string]$description, [string]$style, [hashtable]$test) { $check = [ordered]@{ Description = $description; Style = $style; Includes = $null; Excludes = $null; Text = $false }; foreach ($key in $test.Keys) { $check[$key] = $test[$key] }; $check }
function Rule([string]$original, [string]$replacement, [string]$language = 'auto') { [ordered]@{ Original = $original; Replacement = $replacement; Language = $language } }

# English
Add-Case 'numbers' 'en' 'The battery test on unit [forty-two] finished at [twelve thirty-four] and it did [not] fail.'
Add-Case 'names' 'en' 'Please send the report to [Okafor] and [Lindqvist] before [Friday].' @{ Tags = @('vocabulary-off') }
Add-Case 'names-vocabulary' 'en' 'Please send the report to [Okafor] and [Lindqvist] before [Friday].' @{ Vocabulary = 'Okafor, Lindqvist'; Tags = @('vocabulary-on'); Speaker = 'Ben' }
Add-Case 'technical' 'en' 'Set the timeout to [five hundred] milliseconds and do [not] change the [UART] baud rate.'
Add-Case 'correction-name' 'en' 'Send it to [Peter], sorry, I meant [Maria].' @{ Edited = 'Send it to [Maria].'; Checks = @(
    (Check 'Clean applies a spoken name correction' 'Clean' @{ Includes = 'Maria'; Excludes = 'Peter' }), (Check 'Exact keeps the spoken correction' 'Exact' @{ Includes = 'Peter' })) }
Add-Case 'correction-number' 'en' 'We need [twelve] boards, sorry, I meant [fourteen] boards.' @{ Edited = 'We need [fourteen] boards.'; Checks = @(
    (Check 'Clean applies a spoken number correction' 'Clean' @{ Includes = '14'; Excludes = '12' })) }
Add-Case 'scratch-that' 'en' 'Keep this sentence. The draft is ready. Scratch that.' @{ Edited = 'Keep this sentence.'; Checks = @(
    (Check 'Clean withdraws a sentence after scratch that' 'Clean' @{ Excludes = 'draft' })) }
Add-Case 'fillers' 'en' 'So, um, I think we should, uh, ship it on Friday.' @{ Reference = 'So I think we should ship it on [Friday].' }
Add-Case 'paragraph' 'en' 'First point new paragraph second point comma okay.' @{ SpokenFormatting = $true; Edited = 'First point second point okay.'; Checks = @(
    (Check 'Spoken formatting turns new paragraph into a paragraph break' 'Clean' @{ Includes = "`n`n"; Text = $true })) }
Add-Case 'numbered' 'en' 'Steps numbered item flash the board numbered item reset it.' @{ SpokenFormatting = $true; Edited = 'Steps 1 flash the board 2 reset it.'; Checks = @(
    (Check 'Spoken formatting numbers dictated items' 'Clean' @{ Includes = "`n2. "; Text = $true })) }
Add-Case 'email' 'en' 'Hi Anna, thanks for the update. I will send it by [Friday]. Best regards, Alex.' @{ SpokenFormatting = $true; Checks = @(
    (Check 'Spoken formatting lays out an English email greeting' 'Clean' @{ Includes = "Hi Anna,`n`n"; Text = $true }), (Check 'Spoken formatting lays out an English email closing' 'Concise' @{ Includes = ",`nAlex"; Text = $true })) }
Add-Case 'snippet' 'en' 'Please add my signature at the end.' @{ Snippets = @(Rule 'my signature' "Alex Meyer`nTest Engineer"); Edited = 'Please add Alex Meyer Test Engineer at the end.'; Checks = @(
    (Check 'Clean expands a snippet' 'Clean' @{ Includes = "Alex Meyer`nTest Engineer"; Text = $true }), (Check 'Concise keeps the snippet body' 'Concise' @{ Includes = "Alex Meyer`nTest Engineer"; Text = $true }),
    (Check 'Exact leaves the snippet phrase' 'Exact' @{ Includes = 'my signature' })) }
Add-Case 'alias' 'en' 'I dictated this with private flow today.' @{ Corrections = @(Rule 'private flow' 'PrivateFlow'); Edited = 'I dictated this with PrivateFlow today.'; Checks = @(
    (Check 'Clean applies an alias' 'Clean' @{ Includes = 'PrivateFlow'; Text = $true })) }
Add-Case 'alias-other-language' 'en' 'I dictated this with private flow today.' @{ Corrections = @(Rule 'private flow' 'PrivateFlow' 'de'); Speaker = 'Ben'; Checks = @(
    (Check 'A German alias stays out of English dictation' 'Clean' @{ Excludes = 'PrivateFlow'; Text = $true })) }
Add-Case 'literal' 'en' 'Send it to [Peter], sorry, I meant [Maria].' @{ LiteralMode = $true; Speaker = 'Carl'; Checks = @(
    (Check 'Literal mode keeps the spoken correction' 'Clean' @{ Includes = 'Peter' })) }
Add-Case 'quote' 'en' 'He said quote never trust the build end quote and left.' @{ Edited = 'He said never trust the build and left.'; Checks = @(
    (Check 'Clean turns spoken quote markers into quotation marks' 'Clean' @{ Includes = '"'; Text = $true }), (Check 'Clean drops the spoken end quote' 'Clean' @{ Excludes = 'end quote' }),
    (Check 'Exact keeps the spoken quote markers' 'Exact' @{ Includes = 'end quote' })) }
Add-Case 'accented' 'en' 'We are at [minute forty-two] and the meeting with [Joaquín] is at [nine fifteen].' @{ Speaker = 'Bruno'; Tags = @('accented') }
Add-Case 'long' 'en' ('The release plan changed again this week. The hardware freeze moves to [Tuesday] the [fourteenth], and the firmware branch is cut one day later. ' +
    'We measured [forty-eight] milliamps on the supply rail, which is [not] within the budget of [forty] milliamps. [Priya] will repeat the test on [three] boards tomorrow morning. ' +
    'If the numbers stay that high, we do [not] ship on [Friday], and I will tell the customer by [five] o''clock.') @{ Tags = @('multi-window') }
Add-Case 'room-noise' 'en' '' @{ Clip = 'noise:-60'; Reference = ''; Tags = @('noise') }

# German
Add-Case 'numbers' 'de' 'Der Batterietest an Gerät [zweiundvierzig] war um [zwölf Uhr vierunddreißig] fertig und ist [nicht] fehlgeschlagen.'
Add-Case 'names' 'de' 'Bitte schick den Bericht vor [Freitag] an [Okafor] und [Lindqvist].' @{ Tags = @('vocabulary-off') }
Add-Case 'names-vocabulary' 'de' 'Bitte schick den Bericht vor [Freitag] an [Okafor] und [Lindqvist].' @{ Vocabulary = 'Okafor, Lindqvist'; Tags = @('vocabulary-on'); Speaker = 'Ben' }
Add-Case 'technical' 'de' 'Setz das Timeout auf [fünfhundert] Millisekunden und ändere die [UART] Baudrate [nicht].'
Add-Case 'correction' 'de' 'Wir treffen uns am [Montag], nein, ich meine [Dienstag].' @{ Edited = 'Wir treffen uns am [Dienstag].'; Checks = @(
    (Check 'Clean applies a German spoken correction' 'Clean' @{ Includes = 'Dienstag'; Excludes = 'Montag' })) }
Add-Case 'scratch-that' 'de' 'Dieser Satz bleibt. Der Entwurf ist fertig. Streich das.' @{ Edited = 'Dieser Satz bleibt.'; Checks = @(
    (Check 'Clean withdraws a German sentence after streich das' 'Clean' @{ Excludes = 'Entwurf' })) }
Add-Case 'paragraph' 'de' 'Erste Zeile neuer Absatz zweite Zeile Komma fertig.' @{ SpokenFormatting = $true; Edited = 'Erste Zeile zweite Zeile fertig.'; Checks = @(
    (Check 'German spoken formatting turns neuer Absatz into a paragraph break' 'Clean' @{ Includes = "`n`n"; Text = $true })) }
Add-Case 'email' 'de' 'Hallo Anna, danke für das Update. Ich schicke es bis [Freitag]. Viele Grüße, Alex.' @{ SpokenFormatting = $true; Checks = @(
    (Check 'German email greeting gets its own line' 'Clean' @{ Includes = "Hallo Anna,`n`n"; Text = $true }), (Check 'German email closing gets its own lines' 'Clean' @{ Includes = "Viele Grüße,`nAlex"; Text = $true })) }
Add-Case 'snippet' 'de' 'Füge bitte meine Signatur ein.' @{ Snippets = @(Rule 'meine Signatur' "Viele Grüße`nAlex" 'de'); Edited = 'Füge bitte Viele Grüße Alex ein.'; Checks = @(
    (Check 'A German snippet applies to German dictation' 'Clean' @{ Includes = "Viele Grüße`nAlex"; Text = $true })) }
Add-Case 'long' 'de' ('Der Releaseplan hat sich diese Woche wieder geändert. Der Hardware-Freeze verschiebt sich auf [Dienstag], den [vierzehnten], und der Firmware-Zweig wird einen Tag später abgezweigt. ' +
    'Wir haben [achtundvierzig] Milliampere auf der Versorgung gemessen, das liegt [nicht] im Budget von [vierzig] Milliampere. [Priya] wiederholt den Test morgen früh an [drei] Platinen. ' +
    'Wenn die Werte so hoch bleiben, liefern wir am [Freitag] [nicht] aus, und ich sage es dem Kunden bis [fünf] Uhr.') @{ Tags = @('multi-window') }

# Spanish
Add-Case 'numbers' 'es' 'La prueba de la batería de la unidad [cuarenta y dos] terminó a las [doce y media] y [no] falló.'
Add-Case 'names' 'es' 'Por favor, envía el informe a [Okafor] y a [Lindqvist] antes del [viernes].' @{ Tags = @('vocabulary-off') }
Add-Case 'names-vocabulary' 'es' 'Por favor, envía el informe a [Okafor] y a [Lindqvist] antes del [viernes].' @{ Vocabulary = 'Okafor, Lindqvist'; Tags = @('vocabulary-on'); Speaker = 'Ben' }
Add-Case 'times' 'es' 'Nos vemos el [jueves] a las [nueve y cuarto] en la oficina de [Joaquín].'
Add-Case 'negation' 'es' 'El servidor [no] está listo y faltan [veinte mil] euros.'
Add-Case 'technical' 'es' 'Configura el tiempo de espera en [quinientos] milisegundos y [no] cambies la velocidad del [UART].'
Add-Case 'snippet' 'es' 'Añade mi firma al final.' @{ Snippets = @(Rule 'mi firma' "Saludos,`nAlex" 'es'); Edited = 'Añade Saludos Alex al final.'; Checks = @(
    (Check 'A Spanish snippet applies to Spanish dictation' 'Clean' @{ Includes = "Saludos,`nAlex"; Text = $true })) }
Add-Case 'long' 'es' ('El plan de lanzamiento cambió otra vez esta semana. La congelación del hardware pasa al [martes] [catorce], y la rama del firmware se crea un día después. ' +
    'Medimos [cuarenta y ocho] miliamperios en la alimentación, lo que [no] cabe en el presupuesto de [cuarenta] miliamperios. [Priya] repetirá la prueba mañana con [tres] placas. ' +
    'Si los valores siguen tan altos, [no] entregamos el [viernes], y se lo diré al cliente antes de las [cinco].') @{ Tags = @('multi-window') }

if ($RealSpeech -gt 0) {
    foreach ($taskItem in Get-RealSpeech $RealSpeech) {
        $taskCases.Add([ordered]@{ Name = $taskItem.Name; Language = $taskItem.Language; Clip = $taskItem.Clip; Source = 'real'; Reference = $taskItem.Text; Edited = $null
            SpokenFormatting = $false; LiteralMode = $false; TechnicalMode = $false; Vocabulary = ''; Corrections = @(); Snippets = @(); Checks = @(); Tags = @($taskItem.Gender) })
    }
}
if ($Language) { $taskKept = New-Object System.Collections.Generic.List[object]; $taskCases | Where-Object { $_.Language -eq $Language } | ForEach-Object { $taskKept.Add($_) }; $taskCases = $taskKept }
Invoke-Synthesis
if ($LibraryOutput) {
    $taskSuite = Join-Path $taskRoot 'suite-library.json'
    ConvertTo-Json -InputObject ([ordered]@{ LanguageMode = 'auto'; Cases = $taskCases.ToArray() }) -Depth 8 | Set-Content -LiteralPath $taskSuite -Encoding UTF8
    $taskLibrary = [IO.Path]::GetFullPath($LibraryOutput)
    $null = Invoke-EvalMode $App '--build-audio-library' $taskSuite (Join-Path $taskLibrary 'library.json') $taskData
    Write-Host "Audio library: $taskLibrary ($((Get-Content -LiteralPath (Join-Path $taskLibrary 'library.json') -Raw | ConvertFrom-Json).Clips) clips)"
    return
}
if ($PrepareOnly) { return }

$taskSummary = New-Object System.Collections.Generic.List[string]
function Out-Summary([string]$text) { Write-Host $text; $taskSummary.Add($text) }
foreach ($taskEntry in $Configuration) {
    $null = $taskEntry -match '^(auto|fixed)(-no-detection)?$'
    $taskMode = $Matches[1]; $taskDetection = -not $Matches[2]
    Write-EvalSettings $taskData $taskEngine 'auto' $taskDetection
    $taskSuite = Join-Path $taskRoot "suite-$taskEntry.json"
    ConvertTo-Json -InputObject ([ordered]@{ LanguageMode = $taskMode; Cases = $taskCases.ToArray() }) -Depth 8 | Set-Content -LiteralPath $taskSuite -Encoding UTF8
    $taskReport = Join-Path $taskReports "$($taskEngine.Label)-piper-$taskEntry.json"
    Write-Host "Evaluating dictation: $($taskEngine.Label), configuration $taskEntry ($($taskCases.Count) recordings)..."
    $taskResult = Invoke-EvalMode $App '--evaluate-dictation' $taskSuite $taskReport $taskData

    Out-Summary ''
    Out-Summary "Dictation $taskEntry : model $($taskResult.Model) on $($taskResult.Backend), language $taskMode, speech detection $($taskResult.SpeechDetection), model load $($taskResult.ModelLoadSeconds) s, local polish installed $($taskResult.LocalPolishInstalled)"
    Out-Summary ('{0,-10} {1,-12} {2,-4} {3,6} {4,7} {5,7} {6,9} {7,10} {8,6} {9,9}' -f 'Source', 'Style', 'Lang', 'Cases', 'WER', 'Recall', 'NoFormat', 'Critical', 'Format', 'WrongLang')
    foreach ($taskGroup in $taskResult.Groups | Where-Object { $_.Language }) {
        Out-Summary ('{0,-10} {1,-12} {2,-4} {3,6} {4,7:P1} {5,7:P1} {6,9:P1} {7,10} {8,6} {9,9}' -f $taskGroup.Source, $taskGroup.Style, $taskGroup.Language, $taskGroup.Cases, $taskGroup.Words.WordErrorRate, $taskGroup.Words.WordRecall,
            $taskGroup.Words.RecallIgnoringFormatting, "$($taskGroup.CriticalRecalled)/$($taskGroup.CriticalItems)", $taskGroup.CriticalFormattingOnly, $taskGroup.WrongLanguage)
    }
    $taskNoise = @($taskResult.Cases | Where-Object { $_.Tags -contains 'noise' })
    foreach ($taskCase in $taskNoise) { Out-Summary "Room noise at -60 dBFS produced: '$($taskCase.Raw)'" }
    $taskFailed = @($taskResult.Cases | ForEach-Object { $name = $_.Name; $final = $_.Styles; $_.Checks | Where-Object { -not $_.Passed } | ForEach-Object { $check = $_; "  FAIL $name : $($check.Description) [$($check.Style): '$(($final | Where-Object Style -eq $check.Style).Final.Replace("`n", '\n'))']" } }) +
        @($taskResult.Functional | Where-Object { -not $_.Passed } | ForEach-Object { "  FAIL $($_.Description)" })
    $taskPassed = @($taskResult.Cases | ForEach-Object { $_.Checks } | Where-Object Passed).Count + @($taskResult.Functional | Where-Object Passed).Count
    Out-Summary "Feature checks: $taskPassed passed, $($taskFailed.Count) failed."
    $taskFailed | ForEach-Object { Out-Summary $_ }
    $taskMisses = @($taskResult.Cases | Where-Object Source -eq 'synthetic' | ForEach-Object { $name = $_.Name; ($_.Styles | Where-Object Style -eq 'Exact').Missed | ForEach-Object { "  $name $_" } })
    Out-Summary "Missed critical items in synthetic speech (Exact): $($taskMisses.Count)"
    $taskMisses | Select-Object -First 20 | ForEach-Object { Out-Summary $_ }
}
$taskSummary | Set-Content -LiteralPath (Join-Path $taskRoot "summary-$($taskEngine.Label).txt") -Encoding UTF8
Write-Host "Reports: $taskReports"
