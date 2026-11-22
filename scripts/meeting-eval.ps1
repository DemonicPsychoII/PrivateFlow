# Measures meeting transcription quality on generated conversations and real FLEURS speech
# through the meeting path: the live preview in recognition windows, then the final pass that replaces it.
[CmdletBinding()]
param(
    # The self-contained package; a framework-dependent build stops at a hidden ".NET missing" dialog.
    [string]$App = '',
    # <language>[-no-detection]: language auto, en, de or es; -no-detection turns Silero speech detection off.
    [string[]]$Configuration = @('auto', 'auto-no-detection', 'en'),
    [string]$Scenario = '*',
    # Real FLEURS utterances per language for the real-speech meetings; 0 leaves them out.
    [int]$RealSpeech = 12,
    # Another engine and model than the installed one, for example from an isolated setup-engine.ps1 data root.
    [string]$EnginePath = '', [string]$ModelPath = '', [string]$VadModelPath = '', [string]$EngineLabel = '', [string]$MeetingModelPath = '',
    # final: the live preview, then the final pass (what the app keeps); oracle: every reference utterance transcribed alone in its
    # known language, the recognizer's ceiling.
    [ValidateSet('final', 'oracle')][string]$Mode = 'final',
    # What the app merges into the prompt: the meeting title, the dictation vocabulary and corrections ("Original=Replacement;...").
    [string]$Title = 'Meeting', [string]$DictationVocabulary = '', [string]$Corrections = '',
    # Appended to the report name to keep variants apart.
    [string]$Tag = '',
    # Your own recordings, scored with counts only; none on CI or other PCs. -Scenario none runs only them.
    [string]$Recordings = $env:PRIVATE_FLOW_EVALUATION_RECORDINGS,
    # Downloads the assets and synthesizes the clips of the selected scenarios, then stops.
    [switch]$PrepareOnly
)
. (Join-Path $PSScriptRoot 'eval-common.ps1')
$App = Get-VerificationHost $App
# powershell.exe -File passes a list as one comma-separated string.
$Configuration = @($Configuration | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
foreach ($taskEntry in $Configuration) { if ($taskEntry -notmatch '^(auto|en|de|es)(-no-detection)?$') { throw "Unknown configuration $taskEntry." } }
$taskEngine = Resolve-Engine $EnginePath $ModelPath $VadModelPath $EngineLabel $MeetingModelPath
$taskRoot = Join-Path $EvalOutput 'meeting-eval'
$taskData = Join-Path $taskRoot 'data'; $taskScenarios = Join-Path $taskRoot 'scenarios'; $taskReports = Join-Path $taskRoot 'reports'
foreach ($taskFolder in $taskData, $taskReports) { New-Item -ItemType Directory -Force -Path $taskFolder | Out-Null }
if (Test-Path -LiteralPath $taskScenarios) { Remove-Item -LiteralPath $taskScenarios -Recurse -Force }
New-Item -ItemType Directory -Force -Path $taskScenarios | Out-Null

$taskVoices = Get-VoiceTable

function Say([string]$speaker, [string]$text, [hashtable]$options = @{}) {
    $line = [ordered]@{ Channel = 'mic'; Speaker = $speaker; Language = 'en'; Text = $text; Gap = 0.6; Gain = 0; Tags = @() }
    foreach ($key in $options.Keys) { $line[$key] = $options[$key] }
    $line.Voice = $taskVoices["$speaker|$($line.Language)"]
    if (-not $line.Voice) { throw "No $($line.Language) voice for $speaker." }
    $line.Clip = Get-Clip $line.Voice $text
    $line
}
$taskList = New-Object System.Collections.Generic.List[object]
function Add-Scenario([string]$name, [string]$description, $utterances, $backgrounds = @(), [double]$noiseFloor = -70) {
    $taskList.Add([ordered]@{ Name = $name; Description = $description; NoiseFloor = $noiseFloor; Utterances = @($utterances); Backgrounds = @($backgrounds) })
}
# Lines are speaker, text and optionally language, gap and tags; a language switch from the previous line is tagged.
function Get-Turns($lines, [hashtable]$options = @{}) {
    $previous = $null
    foreach ($line in $lines) {
        $settings = @{ Language = $(if ($line.Count -gt 2) { $line[2] } else { 'en' }) }
        if ($line.Count -gt 3) { $settings.Gap = $line[3] }
        $tags = @(); if ($line.Count -gt 4) { $tags += $line[4] }
        if ($previous -and $previous.Language -ne $settings.Language) { $tags += $(if ($previous.Speaker -eq $line[0]) { 'switch-within-turn' } else { 'switch-between-turns' }) }
        $settings.Tags = $tags
        foreach ($key in $options.Keys) { $settings[$key] = $options[$key] }
        Say $line[0] $line[1] $settings
        $previous = @{ Speaker = $line[0]; Language = $settings.Language }
    }
}

$taskDialog = @{
    en = @(
        @('Alice', 'Good morning everyone, let us start the design review for the sensor board.'),
        @('Ben', 'Before we begin, the battery test finished at [twelve thirty-four] and it did [not] fail.'),
        @('Carl', 'Good. How many units did we run?'),
        @('Ben', 'We ran [forty-two] units over [three] days.'),
        @('Alice', '[Priya] asked whether the firmware update is ready for Friday.'),
        @('Carl', 'It is [not] ready. The bootloader still rejects images larger than [512] kilobytes.'),
        @('Ben', 'Then we should [not] ship on Friday.'),
        @('Alice', 'Agreed. We move the release to [Tuesday].'),
        @('Carl', 'I will send the test report to [Okafor] by [five] o''clock.'),
        @('Alice', 'Let me note that. We are at [minute 42] of the recording, by the way.'),
        @('Ben', 'The noise floor was [sixty] decibels below full scale.'),
        @('Carl', 'Nobody touched the calibration file, right?'),
        @('Alice', 'Right, [nobody] touched it. Keep it that way.'),
        @('Ben', 'One more thing, the meeting with [Lindqvist] is at [nine fifteen].'),
        @('Carl', 'I [won''t] be able to join that one, I am travelling.'),
        @('Alice', 'Fine. Let us close here. Thank you all.'))
    es = @(
        @('Alice', 'Buenos días a todos, empecemos la revisión del diseño de la placa del estetoscopio.', 'es'),
        @('Ben', 'Antes de empezar, la prueba de la batería terminó a las [doce y treinta y cuatro] y [no] falló.', 'es'),
        @('Carl', 'Bien. ¿Cuántas unidades probamos?', 'es'),
        @('Ben', 'Probamos [cuarenta y dos] unidades durante [tres] días.', 'es'),
        @('Alice', '[Priya] preguntó si la actualización del firmware está lista para el viernes.', 'es'),
        @('Carl', '[No] está lista. El cargador de arranque todavía rechaza imágenes de más de [quinientos doce] kilobytes.', 'es'),
        @('Ben', 'Entonces [no] deberíamos entregar el viernes.', 'es'),
        @('Alice', 'De acuerdo. Pasamos la entrega al [martes].', 'es'),
        @('Carl', 'Enviaré el informe de pruebas a [Okafor] antes de las [cinco].', 'es'),
        @('Alice', 'Lo apunto. Por cierto, estamos en el [minuto cuarenta y dos] de la grabación.', 'es'),
        @('Ben', 'El ruido de fondo estaba [sesenta] decibelios por debajo del máximo.', 'es'),
        @('Carl', 'Nadie tocó el archivo de calibración, ¿verdad?', 'es'),
        @('Alice', 'Correcto, [nadie] lo tocó. Que siga así.', 'es'),
        @('Ben', 'Una cosa más, la reunión con [Lindqvist] es a las [nueve y cuarto].', 'es'),
        @('Carl', 'Yo [no] podré asistir a esa, estoy de viaje.', 'es'),
        @('Alice', 'Bien. Terminamos aquí. Gracias a todos.', 'es'))
    de = @(
        @('Alice', 'Guten Morgen zusammen, lasst uns mit dem Design-Review der Sensor-Platine beginnen.', 'de'),
        @('Ben', 'Bevor wir anfangen: Der Batterietest war um [zwölf Uhr vierunddreißig] fertig und ist [nicht] fehlgeschlagen.', 'de'),
        @('Carl', 'Gut. Wie viele Geräte haben wir getestet?', 'de'),
        @('Ben', 'Wir haben [zweiundvierzig] Geräte über [drei] Tage getestet.', 'de'),
        @('Alice', '[Priya] hat gefragt, ob das Firmware-Update bis Freitag fertig ist.', 'de'),
        @('Carl', 'Es ist [nicht] fertig. Der Bootloader lehnt immer noch Images über [fünfhundertzwölf] Kilobyte ab.', 'de'),
        @('Ben', 'Dann sollten wir am Freitag [nicht] ausliefern.', 'de'),
        @('Alice', 'Einverstanden. Wir verschieben das Release auf [Dienstag].', 'de'),
        @('Carl', 'Ich schicke den Testbericht bis [fünf] Uhr an [Okafor].', 'de'),
        @('Alice', 'Ich notiere das. Wir sind übrigens bei [Minute zweiundvierzig] der Aufnahme.', 'de'),
        @('Ben', 'Das Grundrauschen lag [sechzig] Dezibel unter Vollaussteuerung.', 'de'),
        @('Carl', 'Niemand hat die Kalibrierdatei angefasst, oder?', 'de'),
        @('Alice', 'Richtig, [niemand] hat sie angefasst. Das bleibt so.', 'de'),
        @('Ben', 'Noch etwas, das Treffen mit [Lindqvist] ist um [neun Uhr fünfzehn].', 'de'),
        @('Carl', 'Da kann ich [nicht] teilnehmen, ich bin auf Reisen.', 'de'),
        @('Alice', 'Gut. Dann schließen wir hier. Danke euch allen.', 'de'))
}
function Get-Dialog([int]$count = 16, [hashtable]$options = @{}) { Get-Turns ($taskDialog.en | Select-Object -First $count) $options }
$taskShow = @(
    'Good evening and welcome to the news at [seven].',
    'Heavy rain is expected in the north tonight, with up to [forty] millimetres in the mountains.',
    'The city council voted [not] to close the old bridge.',
    'Traffic on the motorway is slow near exit [twenty-three].',
    'In sport, the final starts tomorrow at [eight fifteen].',
    'Tickets sold out in [twelve] minutes.',
    'Now the weather for the weekend, sunny on Saturday and cloudy on Sunday.',
    'Temperatures reach [nineteen] degrees in the afternoon.',
    'That was the news. Stay with us for the evening film.'
)
$taskShowClip = Get-Clip $taskVoices['Narrator|en'] ($taskShow -join ' ')
function Get-ShowOnSystem([int]$repeats) {
    foreach ($repeat in 1..$repeats) { foreach ($sentence in $taskShow) {
        $options = @{ Channel = 'system'; Gap = 0.5; Tags = @('show') }; if ($repeat -eq 1 -and $sentence -eq $taskShow[0]) { $options.At = 0.5 }
        Say 'Narrator' $sentence $options } }
}
# The show plays on the system channel with music; the microphone hears the loudspeakers 25 dB down.
$taskShowBackgrounds = @([ordered]@{ Channel = 'system'; Music = $true; Gain = -14 }, [ordered]@{ Channel = 'mic'; Source = 'system'; Gain = -25 })

Add-Scenario 'three-speakers' 'Three English speakers alternate with short pauses.' (Get-Dialog)
foreach ($taskLanguage in 'es', 'de') {
    Add-Scenario "three-speakers-$taskLanguage" "The same review in $taskLanguage, three speakers." (Get-Turns $taskDialog[$taskLanguage])
}

foreach ($taskSecond in 'es', 'de') {
    $taskLines = if ($taskSecond -eq 'es') { @(
        @('Alice', 'Let us do this one in both languages, as usual.', 'en'),
        @('Ben', 'Perfecto. La reunión con el cliente es el [jueves] a las [doce y media].', 'es'),
        @('Carl', 'Thursday at twelve thirty works for me.', 'en'),
        @('Alice', 'Necesitamos [cuarenta y dos] placas para la prueba.', 'es'),
        @('Ben', 'Okay, so the budget is fine,', 'en'),
        @('Ben', 'pero [no] podemos pagar el envío urgente.', 'es', 0.15),
        @('Carl', '[Joaquín] dice que el servidor [no] está listo.', 'es'),
        @('Alice', 'Entonces lo dejamos para mañana,', 'es'),
        @('Alice', 'and I will tell [Priya] tonight.', 'en', 0.15),
        @('Ben', 'We are at [minute 37], let us wrap up.', 'en'),
        @('Carl', 'Gracias a todos, hasta el [lunes].', 'es')) } else { @(
        @('Alice', 'Let us do this one in both languages, as usual.', 'en'),
        @('Ben', 'Perfekt. Das Treffen mit dem Kunden ist am [Donnerstag] um [zwölf Uhr dreißig].', 'de'),
        @('Carl', 'Thursday at twelve thirty works for me.', 'en'),
        @('Alice', 'Wir brauchen [zweiundvierzig] Platinen für den Test.', 'de'),
        @('Ben', 'Okay, so the budget is fine,', 'en'),
        @('Ben', 'aber den Expressversand können wir [nicht] bezahlen.', 'de', 0.15),
        @('Carl', '[Joachim] sagt, dass der Server [nicht] bereit ist.', 'de'),
        @('Alice', 'Dann verschieben wir es auf morgen,', 'de'),
        @('Alice', 'and I will tell [Priya] tonight.', 'en', 0.15),
        @('Ben', 'We are at [minute 37], let us wrap up.', 'en'),
        @('Carl', 'Danke an alle, bis [Montag].', 'de')) }
    Add-Scenario "code-switching-en-$taskSecond" "English and $taskSecond alternate between and within turns; each language is spoken by that language's voice." (Get-Turns $taskLines)
}

# Mixed-channel speech: three people with Spanish as first language watch a show and discuss it in English and Spanish, between
# and within turns, while the show plays on the system channel; short remarks name timestamps of the show.
$taskOwner = @(
    @('Ana', 'Okay, let us start the episode again from the beginning.', 'en'),
    @('Bruno', 'Vale, pero esta vez tomamos notas de cada escena.', 'es'),
    @('Carmen', 'Good idea. I will write down the times.', 'en'),
    @('Ana', 'Stop there. [Minute four], the [red] car appears.', 'en', 0.6, 'timestamp'),
    @('Bruno', 'Sí, y el conductor [no] lleva cinturón.', 'es'),
    @('Carmen', 'Wait, go back to [twelve thirty-four].', 'en', 0.6, 'timestamp'),
    @('Ana', 'A las [doce treinta y cuatro] se ve la matrícula.', 'es', 0.6, 'timestamp'),
    @('Bruno', 'I cannot read it, the number is [not] clear.', 'en'),
    @('Carmen', 'Es [ocho siete tres].', 'es', 0.4, 'short'),
    @('Ana', 'Good. Next scene, the meeting in the office.', 'en'),
    @('Bruno', 'That is at [minute forty-two], right?', 'en', 0.6, 'timestamp'),
    @('Carmen', '[No], es en el minuto [cuarenta y uno].', 'es', 0.6, 'timestamp'),
    @('Ana', 'Okay, [forty-one].', 'en', 0.4, 'short'),
    @('Bruno', 'Y el jefe dice que [nunca] firmó el contrato.', 'es'),
    @('Carmen', 'But he signed it in episode [three],', 'en'),
    @('Carmen', 'lo vimos el [martes] pasado.', 'es', 0.15),
    @('Ana', 'Right, let me write that down,', 'en'),
    @('Ana', 'el jefe miente en el minuto [cuarenta y dos].', 'es', 0.15, 'timestamp'),
    @('Bruno', '[Nine fifteen].', 'en', 0.4, @('short', 'timestamp')),
    @('Carmen', 'What happens at nine fifteen?', 'en'),
    @('Bruno', 'Entra [Joaquín] con la carta.', 'es'),
    @('Ana', 'Oh yes, and he says the money is [not] there.', 'en'),
    @('Carmen', 'Exacto, faltan [veinte mil] euros.', 'es'),
    @('Bruno', '[Twenty thousand], wow.', 'en', 0.4, 'short'),
    @('Ana', 'Pausa un momento, voy por agua.', 'es'),
    @('Ana', 'I am back. Where are we?', 'en', 9, 'after-silence'),
    @('Carmen', '[Minute fifty-one].', 'en', 0.4, @('short', 'timestamp')),
    @('Bruno', 'Casi al final, quedan [nueve] minutos.', 'es'),
    @('Ana', 'Okay, let us finish it and talk tomorrow at [ten thirty].', 'en', 0.6, 'timestamp'),
    @('Carmen', 'Perfecto, mañana a las [diez y media].', 'es', 0.6, 'timestamp'))
Add-Scenario 'mixed-channel-en-es' 'Three speakers with Spanish as first language discuss a show in English and Spanish, between and within turns, with short timestamp remarks, while the show plays on the system channel with music and leaks into the mic 25 dB down.' (@(Get-Turns $taskOwner) + @(Get-ShowOnSystem 3)) $taskShowBackgrounds
Add-Scenario 'mixed-channel-en-es-mic-only' 'The same discussion without the show, to separate the show''s effect from the speech itself.' (Get-Turns $taskOwner)
# The same speakers in a call: one of them on the microphone, the other two arrive with the show on the system channel.
function ConvertTo-RemoteLayout($utterances) {
    foreach ($utterance in $utterances) { $utterance.Timeline = 'people'; if ($utterance.Speaker -ne 'Ana') { $utterance.Channel = 'system' }; $utterance }
}
Add-Scenario 'mixed-channel-en-es-shared-system' 'A discussion with only Ana on the microphone; Bruno, Carmen and the show arrive mixed on the system channel, which leaks into the mic 25 dB down.' (@(ConvertTo-RemoteLayout (Get-Turns $taskOwner)) + @(Get-ShowOnSystem 3)) $taskShowBackgrounds
# More spoken times by the same people, so the share of times found resolves to a few points: clock times and minute marks in both languages.
$taskOwnerTimes = @(
    @('Ana', 'Okay, second episode. I will call out the times again.', 'en'),
    @('Bruno', 'Vale. La primera escena empieza en el [minuto dos].', 'es', 0.6, 'timestamp'),
    @('Carmen', 'At [minute three] the train leaves the station.', 'en', 0.6, 'timestamp'),
    @('Ana', '[Minute five], she looks at the clock.', 'en', 0.6, 'timestamp'),
    @('Bruno', 'The clock says [seven forty-five].', 'en', 0.6, 'timestamp'),
    @('Carmen', 'No, pone las [siete y cuarenta].', 'es', 0.6, 'timestamp'),
    @('Ana', 'Go back to [six twenty].', 'en', 0.4, @('short', 'timestamp')),
    @('Bruno', 'Ahí, en el [minuto seis], se ve el andén.', 'es', 0.6, 'timestamp'),
    @('Carmen', 'The train is due at [eight ten].', 'en', 0.6, 'timestamp'),
    @('Ana', 'Y llega a las [ocho y veinticinco].', 'es', 0.6, 'timestamp'),
    @('Bruno', 'That is at [minute nine], I think.', 'en', 0.6, 'timestamp'),
    @('Carmen', '[Minute eleven].', 'en', 0.4, @('short', 'timestamp')),
    @('Ana', 'Minuto [once], sí.', 'es', 0.4, @('short', 'timestamp')),
    @('Bruno', 'Let us skip to [minute fourteen].', 'en', 0.6, 'timestamp'),
    @('Carmen', 'Pausa en el [minuto quince].', 'es', 0.6, 'timestamp'),
    @('Ana', 'The letter arrives at [ten fifty].', 'en', 0.6, 'timestamp'),
    @('Bruno', 'La carta llega a las [diez y cincuenta].', 'es', 0.6, 'timestamp'),
    @('Carmen', '[Minute eighteen], the phone rings.', 'en', 0.6, 'timestamp'),
    @('Ana', 'Who calls at [eleven fifteen]?', 'en', 0.6, 'timestamp'),
    @('Bruno', 'Su hermano, a las [once y cuarto].', 'es', 0.6, 'timestamp'),
    @('Carmen', 'Go back to [minute twenty].', 'en', 0.6, 'timestamp'),
    @('Ana', 'En el [minuto veintidós] cambia la música.', 'es', 0.6, 'timestamp'),
    @('Bruno', 'The meeting in the film starts at [twelve twenty].', 'en', 0.6, 'timestamp'),
    @('Carmen', 'Sí, la reunión es a las [doce y veinte].', 'es', 0.6, 'timestamp'),
    @('Ana', '[Minute twenty-four].', 'en', 0.4, @('short', 'timestamp')),
    @('Bruno', 'No, it is [minute twenty-six].', 'en', 0.6, 'timestamp'),
    @('Carmen', 'Estamos en el [minuto veintiocho].', 'es', 0.6, 'timestamp'),
    @('Ana', 'The boss leaves at [one thirty].', 'en', 0.6, 'timestamp'),
    @('Bruno', 'A la [una y media] se va el jefe.', 'es', 0.6, 'timestamp'),
    @('Carmen', 'At [minute thirty-one] the lights go out.', 'en', 0.6, 'timestamp'),
    @('Ana', '[Minuto treinta y tres].', 'es', 0.4, @('short', 'timestamp')),
    @('Bruno', 'We are at [minute thirty-five] now.', 'en', 0.6, 'timestamp'),
    @('Carmen', 'The alarm is set for [two forty-five].', 'en', 0.6, 'timestamp'),
    @('Ana', 'La alarma suena a las [dos y cuarenta y cinco].', 'es', 0.6, 'timestamp'),
    @('Bruno', '[Minute thirty-eight], the police arrive.', 'en', 0.6, 'timestamp'),
    @('Carmen', 'En el [minuto cuarenta] hablan con el jefe.', 'es', 0.6, 'timestamp'),
    @('Ana', 'He says he was home at [nine thirty].', 'en', 0.6, 'timestamp'),
    @('Bruno', 'Mentira, a las [nueve y media] estaba en la oficina.', 'es', 0.6, 'timestamp'),
    @('Carmen', '[Minute forty-four].', 'en', 0.4, @('short', 'timestamp')),
    @('Ana', 'Okay, the last scene is at [minute forty-seven].', 'en', 0.6, 'timestamp'),
    @('Bruno', 'Termina en el [minuto cincuenta y dos].', 'es', 0.6, 'timestamp'),
    @('Carmen', 'Let us meet again on Thursday at [four fifteen].', 'en', 0.6, 'timestamp'),
    @('Ana', 'Perfecto, el jueves a las [cuatro y cuarto].', 'es', 0.6, 'timestamp'))
Add-Scenario 'mixed-channel-times-en-es' 'The same three people call out 42 more times of a show, in English and Spanish, while the show plays on the system channel and leaks into the mic.' (@(Get-Turns $taskOwnerTimes) + @(Get-ShowOnSystem 4)) $taskShowBackgrounds
Add-Scenario 'mixed-channel-times-en-es-shared-system' 'The same times with only Ana on the microphone; Bruno, Carmen and the show arrive mixed on the system channel.' (@(ConvertTo-RemoteLayout (Get-Turns $taskOwnerTimes)) + @(Get-ShowOnSystem 4)) $taskShowBackgrounds
# Someone on the microphone confirms what the system channel just said. The microphone also hears the system channel 25 dB down
# at the same moment: that echo should go, the reply after it must stay. Lines are speaker, channel, language, text and optionally
# 'confirmation'; all follow each other on one timeline.
function Get-Confirmations($lines) {
    foreach ($line in $lines) {
        $tags = @(); if ($line[0] -eq 'Narrator') { $tags += 'show' }; if ($line.Count -gt 4) { $tags += $line[4] }
        Say $line[0] $line[3] @{ Channel = $line[1]; Language = $line[2]; Timeline = 'people'; Gap = 0.4; Tags = $tags }
    }
}
Add-Scenario 'confirmations-show-on-system' 'Ana, Bruno and Carmen on the microphone repeat times and numbers the show on the system channel just said, in English and Spanish.' (Get-Confirmations @(
    @('Narrator', 'system', 'en', 'Good evening. The match starts tomorrow at [eight fifteen].'),
    @('Ana', 'mic', 'en', '[Eight fifteen] tomorrow, okay.', 'confirmation'),
    @('Narrator', 'system', 'en', 'The council meets on [Friday] at [twelve thirty].'),
    @('Bruno', 'mic', 'en', 'At [twelve thirty]?', 'confirmation'),
    @('Narrator', 'system', 'en', 'Tickets cost [forty-five] euros at the door.'),
    @('Carmen', 'mic', 'es', '[Cuarenta y cinco] euros, vale.', 'confirmation'),
    @('Narrator', 'system', 'en', 'The evening film starts at [seven forty].'),
    @('Ana', 'mic', 'es', 'A las [siete y cuarenta], vale.', 'confirmation'),
    @('Narrator', 'system', 'en', 'Send your questions to the studio by [Friday].'),
    @('Bruno', 'mic', 'en', 'By [Friday], okay.', 'confirmation'),
    @('Carmen', 'mic', 'en', 'Let us watch the film then.'),
    @('Narrator', 'system', 'en', 'Traffic is slow near exit [twenty-three] tonight.'),
    @('Ana', 'mic', 'en', 'Exit [twenty-three], right, we take the other road.', 'confirmation'))) $taskShowBackgrounds
Add-Scenario 'confirmations-shared-system' 'Ana alone on the microphone confirms what Bruno, Carmen and the show on the system channel just said, in English and Spanish.' (Get-Confirmations @(
    @('Bruno', 'system', 'en', 'So we should meet tomorrow at [twelve thirty].'),
    @('Ana', 'mic', 'en', 'Tomorrow at [twelve thirty], okay.', 'confirmation'),
    @('Carmen', 'system', 'en', 'Can you send me the report by [Friday]?'),
    @('Ana', 'mic', 'en', 'By [Friday], okay.', 'confirmation'),
    @('Narrator', 'system', 'en', 'In sport, the final starts tomorrow at [eight fifteen].'),
    @('Ana', 'mic', 'en', '[Eight fifteen] tomorrow, yes.', 'confirmation'),
    @('Bruno', 'system', 'es', 'La película empieza a las [siete y cuarenta].'),
    @('Ana', 'mic', 'es', 'A las [siete y cuarenta], vale.', 'confirmation'),
    @('Carmen', 'system', 'es', 'Necesitamos [cuarenta y dos] entradas.'),
    @('Ana', 'mic', 'es', '[Cuarenta y dos] entradas, perfecto.', 'confirmation'),
    @('Narrator', 'system', 'en', 'Traffic is slow near exit [twenty-three] tonight.'),
    @('Ana', 'mic', 'en', 'Exit [twenty-three], right.', 'confirmation'),
    @('Bruno', 'system', 'en', 'The meeting in the film starts at [twelve twenty].'),
    @('Ana', 'mic', 'en', 'At [twelve twenty], got it.', 'confirmation'))) $taskShowBackgrounds

Add-Scenario 'quiet-interjections' 'One speaker leads; short remarks with numbers, times, names and negations come in at normal level and 18 or 24 dB quieter.' @(
    Say 'Alice' 'Let me walk you through the schedule for the next release, because a few dates moved since last week.'
    Say 'Ben' 'We are at [minute 42].' @{ Gain = -18; Gap = 0.3; Tags = @('quiet', 'short', 'timestamp') }
    Say 'Alice' 'The hardware freeze is still planned, and the test lab confirmed the booking for the climate chamber.'
    Say 'Carl' '[Twelve thirty-four].' @{ Gain = -24; Gap = 0.3; Tags = @('quiet', 'short', 'timestamp') }
    Say 'Alice' 'The firmware branch will be cut after the integration tests pass on all three boards.'
    Say 'Ben' '[Nine fifteen].' @{ Gap = 0.3; Tags = @('short', 'timestamp') }
    Say 'Alice' 'I would like to present the results to the steering group on Tuesday.'
    Say 'Carl' '[Not] Tuesday.' @{ Gap = 0.3; Tags = @('short') }
    Say 'Alice' 'Fine, then we take the slot on Friday afternoon instead.'
    Say 'Ben' '[No], [Thursday].' @{ Gain = -18; Gap = 0.3; Tags = @('quiet', 'short') }
    Say 'Alice' 'Thursday it is. The report needs the final numbers from the battery run.'
    Say 'Carl' '[Seventeen] units.' @{ Gain = -24; Gap = 0.3; Tags = @('quiet', 'short') }
    Say 'Alice' 'That is enough for the statistics. Who is collecting the data?'
    Say 'Ben' '[Okafor].' @{ Gap = 0.3; Tags = @('short') }
    Say 'Alice' 'Good, then the last open item is the supplier contract.'
    Say 'Carl' '[Lindqvist] has it.' @{ Gain = -18; Gap = 0.3; Tags = @('quiet', 'short') }
    Say 'Alice' 'Thanks. That is all for today.'
)

Add-Scenario 'overlapping-speech' 'Speakers start before the previous one finishes, by 0.8 to 2.5 seconds.' @(
    Say 'Alice' 'The review found two issues in the power supply design that we need to fix before the next build.'
    Say 'Ben' 'Yes, and the second one is the [missing] fuse on the battery input.' @{ Gap = -1.5; Tags = @('overlap') }
    Say 'Carl' 'How long will the fix take?'
    Say 'Alice' 'About [three] days, if the parts arrive on [Monday].' @{ Gap = -0.8; Tags = @('overlap') }
    Say 'Ben' 'I can order them today, it costs [eighty-five] euros.' @{ Gap = -2.5; Tags = @('overlap') }
    Say 'Carl' 'Then we keep the build date and [do not] move the release.'
    Say 'Alice' 'Agreed, I will note the order time as [twelve thirty-four].' @{ Gap = -1.2; Tags = @('overlap') }
    Say 'Ben' 'And we are at [minute 42], so let us stop here.' @{ Gap = -1.0; Tags = @('overlap') }
)

Add-Scenario 'long-silences' 'Pauses of 30 to 70 seconds with room noise at -60 dBFS between remarks.' @(
    Say 'Alice' 'Let us take a short break and continue when everyone is back.'
    Say 'Ben' 'I am back. The coffee machine is broken again.' @{ Gap = 45; Tags = @('after-silence') }
    Say 'Carl' 'Sorry for the delay, the call with [Okafor] took longer.' @{ Gap = 70; Tags = @('after-silence') }
    Say 'Alice' 'No problem. Where were we? We are at [minute 51].' @{ Gap = 2 }
    Say 'Ben' 'The test result came in, [nineteen] of [twenty] boards passed.' @{ Gap = 30; Tags = @('after-silence') }
    Say 'Carl' 'Then we are [not] blocked.' @{ Gap = 1 }
) -noiseFloor -60

Add-Scenario 'chunk-boundaries' 'Utterances centred on the 0.5 s overlap of consecutive 20 s windows, with controls between the cuts.' @(
    Say 'Alice' 'The first measurement shows a supply current of [forty-eight] milliamps.' @{ Around = 9; Tags = @('control') }
    Say 'Ben' 'We are at [minute 42] and the second board just [failed] the drop test.' @{ Around = 19.75 }
    Say 'Carl' 'I will write that down in the test log right now.' @{ Around = 30; Tags = @('control') }
    Say 'Alice' 'Please call [Priya] at [twelve thirty-four] and tell her the result is [not] final.' @{ Around = 39.25 }
    Say 'Ben' 'The third board is still running in the climate chamber.' @{ Around = 49; Tags = @('control') }
    Say 'Carl' 'The chamber log says [two hundred fourteen] cycles without a single reset.' @{ Around = 58.75 }
    Say 'Alice' 'That is better than the previous revision.' @{ Around = 68; Tags = @('control') }
    Say 'Ben' 'We should [not] ship before [Lindqvist] signs the report on [Friday].' @{ Around = 78.25 }
    Say 'Carl' 'Agreed, I will remind him tomorrow.' @{ Around = 88; Tags = @('control') }
)

foreach ($taskLevel in -30, -20, -12) {
    Add-Scenario "show-under-speech$taskLevel`db" "Twelve dialogue turns over a news show (narration and music) on the mic, the show $(-$taskLevel) dB below the voices." (Get-Dialog 12) @(
        [ordered]@{ Channel = 'mic'; Clip = $taskShowClip; Gain = $taskLevel }
        [ordered]@{ Channel = 'mic'; Music = $true; Gain = $taskLevel - 6 }
    )
}
Add-Scenario 'show-on-system-channel' 'The news show plays on the system channel with music; the mic hears it 25 dB down while three people talk.' (@(Get-Dialog 12) + @(Get-ShowOnSystem 2)) $taskShowBackgrounds

foreach ($taskLevel in -30, -40) {
    Add-Scenario "low-level-speech$taskLevel`db" "Twelve dialogue turns $(-$taskLevel) dB below normal speech level (active level $(-20 + $taskLevel) dBFS) over -70 dBFS noise." (Get-Dialog 12 @{ Gain = $taskLevel })
}

$taskNames = 'Priya', 'Okafor', 'Lindqvist', 'Mariana', 'Tobias', 'Keiko'
$taskDays = 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday'
$taskTemplates = @(
    'The battery test on unit [{n}] finished at [{t}].',
    'We are at [minute {m}] of this meeting.',
    '[{p}] will [not] attend the review on [{d}].',
    'Please do [not] change the calibration file before [{d}].',
    'The bootloader accepts images up to [{n}] kilobytes.',
    'I measured [{n}] millivolts on the reference pin.',
    '[{p}] said the supplier ships [{n}] boards next week.',
    'That is fine, but [no] firmware goes out without the review.',
    'Let us move the call with [{p}] to [{t}].',
    'I [never] saw that error on the old revision.',
    'The log shows [{n}] resets during the night.',
    'Okay, noted. Next point.',
    'Can you send me the report by [{d}]?',
    'Yes, I will send it to [{p}] as well.'
)
$taskSpeakers = 'Alice', 'Ben', 'Carl'
$taskEndurance = for ($taskTurn = 0; $taskTurn -lt 150; $taskTurn++) {
    $taskNumber = 3 + ($taskTurn * 37) % 950
    $taskTime = "$(1 + ($taskTurn * 5) % 12):$(11 + ($taskTurn * 13) % 48)"
    $taskText = $taskTemplates[($taskTurn * 5) % $taskTemplates.Count].Replace('{n}', $taskNumber).Replace('{t}', $taskTime).Replace('{m}', [math]::Floor($taskTurn / 11) + 1).Replace('{p}', $taskNames[$taskTurn % $taskNames.Count]).Replace('{d}', $taskDays[$taskTurn % $taskDays.Count])
    $taskOptions = @{}
    if ($taskTurn % 9 -eq 4) { $taskOptions = @{ Gain = -18; Gap = 0.3; Tags = @('quiet') } }
    if ($taskTurn % 40 -eq 39) { $taskOptions = @{ Gap = 25; Tags = @('after-silence') } }
    Say $taskSpeakers[($taskTurn * 2 + [math]::Floor($taskTurn / 3)) % 3] $taskText $taskOptions
}
Add-Scenario 'endurance-10-minutes' 'About ten minutes of three speakers with templated numbers, times, names and negations, quiet remarks and two 25 s pauses.' $taskEndurance

# Real speech: FLEURS speakers in turn, as a meeting would hear them; English and Spanish alternate, German separately.
if ($RealSpeech -gt 0) {
    $taskReal = Get-RealSpeech $RealSpeech
    $taskEnglish = @($taskReal | Where-Object Language -eq 'en'); $taskSpanish = @($taskReal | Where-Object Language -eq 'es')
    $taskMixed = for ($taskIndex = 0; $taskIndex -lt [math]::Min($taskEnglish.Count, $taskSpanish.Count); $taskIndex++) { $taskEnglish[$taskIndex]; $taskSpanish[$taskIndex] }
    function ConvertTo-RealTurns($items) {
        $previous = $null
        foreach ($item in $items) {
            $tags = @('real-speech'); if ($previous -and $previous -ne $item.Language) { $tags += 'switch-between-turns' }
            [ordered]@{ Channel = 'mic'; Speaker = $item.Gender; Language = $item.Language; Text = $item.Text; Clip = $item.Clip; Gap = 0.6; Gain = 0; Tags = $tags }
            $previous = $item.Language
        }
    }
    Add-Scenario 'real-speech-en-es' "Real read speech (FLEURS): $(@($taskMixed).Count) English and Spanish utterances by different speakers in turn." (ConvertTo-RealTurns $taskMixed)
    Add-Scenario 'real-speech-en-es-show' 'The same real utterances while the news show plays on the system channel and leaks into the mic.' (@(ConvertTo-RealTurns $taskMixed) + @(Get-ShowOnSystem 2)) $taskShowBackgrounds
    Add-Scenario 'real-speech-de' "Real read speech (FLEURS): $(@($taskReal | Where-Object Language -eq 'de').Count) German utterances in turn." (ConvertTo-RealTurns @($taskReal | Where-Object Language -eq 'de'))
}

$taskPatterns = @($Scenario -split ',' | Where-Object { $_ })
$taskSelected = @($taskList | Where-Object { $name = $_.Name; @($taskPatterns | Where-Object { $name -like $_ }).Count -gt 0 })
$taskRecordings = $(if ($Recordings -and (Test-Path -LiteralPath $Recordings -PathType Container)) { (Resolve-Path -LiteralPath $Recordings).Path })
if ($taskSelected.Count -eq 0 -and -not $taskRecordings) { throw "No scenario matches $Scenario." }
if ($taskSelected.Count -gt 0) { Invoke-Synthesis }
if ($PrepareOnly) { return }
foreach ($taskEntry in $taskSelected) {
    $taskFolder = Join-Path $taskScenarios $taskEntry.Name; New-Item -ItemType Directory -Force -Path $taskFolder | Out-Null
    ConvertTo-Json -InputObject $taskEntry -Depth 8 | Set-Content -LiteralPath (Join-Path $taskFolder 'scenario.json') -Encoding UTF8
}

$taskSummary = New-Object System.Collections.Generic.List[string]
function Out-Summary([string]$text) { Write-Host $text; $taskSummary.Add($text) }
foreach ($taskEntry in @($Configuration | Where-Object { $taskSelected.Count -gt 0 })) {
    $null = $taskEntry -match '^(auto|en|de|es)(-no-detection)?$'
    $taskCorrections = @($Corrections -split ';' | Where-Object { $_ -match '=' } | ForEach-Object { $original, $replacement = $_ -split '=', 2; [ordered]@{ Original = $original.Trim(); Replacement = $replacement.Trim(); Language = 'auto' } })
    Write-EvalSettings $taskData $taskEngine $Matches[1] (-not $Matches[2]) -Extra @{ Vocabulary = $DictationVocabulary; Corrections = $taskCorrections }
    $taskName = "$($taskEngine.Label)-piper-$taskEntry" + $(if ($Mode -ne 'final') { "-$Mode" }) + $(if ($Tag) { "-$Tag" })
    $taskReport = Join-Path $taskReports "$taskName.json"
    $taskOptions = Join-Path $taskRoot "options-$taskName.json"
    ConvertTo-Json -InputObject ([ordered]@{ Mode = $Mode; Title = $Title }) | Set-Content -LiteralPath $taskOptions -Encoding UTF8
    Write-Host "Evaluating meetings: $($taskEngine.Label), configuration $taskEntry, mode $Mode..."
    $taskResult = Invoke-EvalMode $App '--evaluate-meeting' $taskScenarios $taskReport $taskData -Extra @("`"$taskOptions`"")

    Out-Summary ''
    Out-Summary "Configuration $taskEntry : model $($taskResult.Model) on $($taskResult.Backend), language $($taskResult.Language), speech detection $($taskResult.SpeechDetection), model load $($taskResult.ModelLoadSeconds) s, mode $Mode"
    Out-Summary ('{0,-30} {1,7} {2,6} {3,6} {4,9} {5,6} {6,9} {7,6} {8,7}' -f 'Scenario', 'Seconds', 'WER', 'Recall', 'Critical', 'Format', 'WrongLang', 'Ins', 'RTF')
    foreach ($taskScore in $taskResult.Scenarios) {
        Out-Summary ('{0,-30} {1,7:0} {2,6:P0} {3,6:P0} {4,9} {5,6} {6,9} {7,6} {8,7:0.000}' -f $taskScore.Name, $taskScore.Seconds, $taskScore.Words.WordErrorRate, $taskScore.Words.WordRecall,
            "$($taskScore.CriticalRecalled)/$($taskScore.CriticalItems)", $taskScore.CriticalFormattingOnly, "$($taskScore.WrongLanguageWindows)/$(@($taskScore.Windows).Count)", $taskScore.Words.Insertions, $taskScore.RealTimeFactor)
    }
    Out-Summary 'Format: critical misses that are formatting only. WrongLang: windows or runs in another language than spoken. Ins: inserted words.'
    $taskGroups = $taskResult.Scenarios | ForEach-Object { $_.Tags + $_.Languages | ForEach-Object { $_ } } | Group-Object Name
    Out-Summary ('{0,-28} {1,7} {2,7} {3,9} {4,10} {5,6}' -f 'Tag or language (all)', 'Words', 'Recall', 'NoFormat', 'Critical', 'Format')
    foreach ($taskGroup in $taskGroups) {
        $taskWords = ($taskGroup.Group | ForEach-Object { $_.Words.ReferenceWords } | Measure-Object -Sum).Sum
        $taskMatched = ($taskGroup.Group | ForEach-Object { $_.Words.ReferenceWords - $_.Words.Substitutions - $_.Words.Deletions } | Measure-Object -Sum).Sum
        $taskFormatting = ($taskGroup.Group | ForEach-Object { $_.Words.FormattingOnly } | Measure-Object -Sum).Sum
        $taskItems = ($taskGroup.Group | ForEach-Object { $_.CriticalItems } | Measure-Object -Sum).Sum
        $taskRecalled = ($taskGroup.Group | ForEach-Object { $_.CriticalRecalled } | Measure-Object -Sum).Sum
        $taskItemFormatting = ($taskGroup.Group | ForEach-Object { $_.CriticalFormattingOnly } | Measure-Object -Sum).Sum
        Out-Summary ('{0,-28} {1,7} {2,7:P1} {3,9:P1} {4,10} {5,6}' -f $taskGroup.Name, $taskWords, $(if ($taskWords) { $taskMatched / $taskWords } else { 1 }), $(if ($taskWords) { ($taskMatched + $taskFormatting) / $taskWords } else { 1 }), "$taskRecalled/$taskItems", $taskItemFormatting)
    }
    $taskPreview = @($taskResult.Scenarios | Where-Object Preview | ForEach-Object Preview)
    if ($taskPreview.Count) {
        $taskWords = ($taskPreview | ForEach-Object { $_.Words.ReferenceWords } | Measure-Object -Sum).Sum
        $taskMatched = ($taskPreview | ForEach-Object { $_.Words.ReferenceWords - $_.Words.Substitutions - $_.Words.Deletions } | Measure-Object -Sum).Sum
        Out-Summary ("Live preview: words {0:P1}, critical {1}/{2}, wrong-language windows {3}, slowest window {4:0.00} s" -f ($taskMatched / [math]::Max(1, $taskWords)),
            ($taskPreview | Measure-Object CriticalRecalled -Sum).Sum, ($taskPreview | Measure-Object CriticalItems -Sum).Sum, ($taskPreview | Measure-Object WrongLanguageWindows -Sum).Sum, ($taskPreview | Measure-Object LongestWindowSeconds -Maximum).Maximum)
    }
    $taskMissed = @($taskResult.Scenarios | ForEach-Object { $name = $_.Name; $_.Missed | ForEach-Object { "  $name [$($_.Channel) $($_.Start)s $($_.Tags -join ',')]$(if ($_.FormattingOnly) { ' (format)' }) '$($_.Item)' heard '$($_.HeardForItem)' in '$($_.HeardForUtterance)'" } })
    Out-Summary "Missed critical items: $($taskMissed.Count), $(@($taskResult.Scenarios.Missed | Where-Object FormattingOnly).Count) of them formatting only (first 25; all in $taskReport)"
    $taskMissed | Select-Object -First 25 | ForEach-Object { Out-Summary $_ }
    # Goals 1 and 3 need the oracle and the vocabulary runs; full-eval.ps1 prints them.
    if ($Mode -eq 'final') { Get-MeetingGoalLines $taskResult | ForEach-Object { Out-Summary $_ } }
}
if ($taskRecordings) {
    Write-EvalSettings $taskData $taskEngine 'auto' $true
    $taskReport = Join-Path $taskReports "$($taskEngine.Label)-recordings$(if ($Tag) { "-$Tag" }).json"
    Write-Host "Evaluating own recordings: $($taskEngine.Label)..."
    $taskResult = Invoke-EvalMode $App '--evaluate-recordings' $taskRecordings $taskReport $taskData -TimeoutMinutes 240
    Out-Summary ''
    Get-RecordingLines $taskResult | ForEach-Object { Out-Summary $_ }
}
$taskSummary | Set-Content -LiteralPath (Join-Path $taskRoot "summary-$($taskEngine.Label)$(if ($taskSelected.Count -eq 0) { '-recordings' })$(if ($Mode -ne 'final') { "-$Mode" })$(if ($Tag) { "-$Tag" }).txt") -Encoding UTF8
Write-Host "Reports: $taskReports"
