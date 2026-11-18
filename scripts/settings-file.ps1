# The settings step of setup-engine.ps1, setup-rewrite.ps1 and install.ps1 when no Private Flow window started them, dot-sourced by all
# three. settings.json of a data folder has one writer at a time: whoever reads or writes it holds settings.lock open without sharing,
# from the read to the rename, and a running Private Flow holds instance.lock open for its lifetime. Windows releases both when their
# process ends; the files stay. While Private Flow runs on the folder, nothing here writes its settings.

function Open-Exclusive([string]$File) { [IO.File]::Open($File, 'OpenOrCreate', 'ReadWrite', 'None') }

function Test-PrivateFlowRunning([string]$DataRoot) {
    New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
    try { (Open-Exclusive (Join-Path $DataRoot 'instance.lock')).Dispose(); $false }
    catch { if ($_.Exception.GetBaseException() -is [IO.IOException]) { $true } else { throw } }
}

$PrivateFlowRunningMessage = 'Private Flow is running on this data folder. Close it and run this again, or install from Settings > Models in Private Flow.'

function Save-InstalledSettings([string]$DataRoot, [Collections.IDictionary]$Values) {
    New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
    $lock = $null
    for ($attempt = 1; -not $lock; $attempt++) {
        try { $lock = Open-Exclusive (Join-Path $DataRoot 'settings.lock') }
        catch {
            if ($attempt -ge 600 -or $_.Exception.GetBaseException() -isnot [IO.IOException]) { throw "The settings stayed locked by another program; nothing was written. $($_.Exception.Message)" }
            Start-Sleep -Milliseconds 50
        }
    }
    try {
        if (Test-PrivateFlowRunning $DataRoot) { throw "$PrivateFlowRunningMessage The new paths were not stored." }
        $file = Join-Path $DataRoot 'settings.json'
        $config = if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json } else { [PSCustomObject]@{} }
        foreach ($name in $Values.Keys) { $config | Add-Member -NotePropertyName $name -NotePropertyValue $Values[$name] -Force }
        $partial = "$file.setup-$([Guid]::NewGuid().ToString('N')).partial"
        $bytes = (New-Object Text.UTF8Encoding $false).GetBytes(($config | ConvertTo-Json -Depth 20))
        $stream = [IO.File]::Open($partial, 'CreateNew', 'Write', 'None')
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        Move-Item -LiteralPath $partial -Destination $file -Force
    } finally { $lock.Dispose() }
}
