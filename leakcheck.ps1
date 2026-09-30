<#
.SYNOPSIS
  Leak check. Runs the scripted 5-turn interview (3 PII-rich demo inputs + repeat person + memory probe), a resumed turn,
  a context-budget conversation and a JSONL export through the headless self-test, then searches EVERY file the app
  wrote (SQLite database + journal, app logs, exported JSONL, salt file) for EVERY original PII value.
  Zero matches are required - this includes titles and rolling summaries, which live in the database.

.PARAMETER SkipRun   Only scan an existing data folder (e.g. your real %LOCALAPPDATA%\KnowledgeCapture).
.PARAMETER DataDir   Folder to run in / scan. Default: a fresh folder under %TEMP%.
#>
param(
    [switch]$SkipRun,
    [string]$DataDir
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $DataDir) { $DataDir = Join-Path $env:TEMP ("kc-leakcheck-" + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$export = Join-Path $DataDir 'export.jsonl'

Write-Host '== Building the headless self-test' -ForegroundColor Cyan
dotnet build App.SelfTest\App.SelfTest.csproj -c Release -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$exe = Join-Path $PSScriptRoot 'App.SelfTest\bin\Release\net8.0-windows\KnowledgeCapture.SelfTest.exe'

$selfExit = 0
if (-not $SkipRun) {
    Write-Host "== Scripted interview + self-tests (data dir: $DataDir)" -ForegroundColor Cyan
    & $exe --data-dir $DataDir --export $export
    $selfExit = $LASTEXITCODE
}

Write-Host "`n== Searching every stored file for original PII values" -ForegroundColor Cyan
$pii = @(& $exe --print-pii | Where-Object { $_.Trim().Length -gt 0 })
$files = @(Get-ChildItem -LiteralPath $DataDir -Recurse -File -ErrorAction SilentlyContinue)
if ($files.Count -eq 0) { throw "No files found in $DataDir" }

$utf8 = [System.Text.Encoding]::UTF8
$utf16 = [System.Text.Encoding]::Unicode
$total = 0
$rows = foreach ($f in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    # SQLite stores UTF-8 text; also decode as UTF-16 in case of a UTF-16 database or journal
    $texts = @($utf8.GetString($bytes), $utf16.GetString($bytes))
    $hits = @($pii | Where-Object { $v = $_; $texts | Where-Object { $_.IndexOf($v, [StringComparison]::OrdinalIgnoreCase) -ge 0 } })
    $total += $hits.Count
    [pscustomobject]@{
        File    = $f.FullName.Substring($DataDir.Length).TrimStart('\')
        Bytes   = $f.Length
        Matches = $hits.Count
        Values  = ($hits | Select-Object -Unique) -join ', '
    }
}
$rows | Format-Table -AutoSize -Wrap | Out-String -Width 220 | Write-Host
Write-Host ("Checked {0} PII values x {1} files" -f $pii.Count, $files.Count)

$needed = @('knowledge.db', 'export.jsonl')
foreach ($n in $needed) {
    if (-not ($files | Where-Object Name -eq $n)) { Write-Host "WARNING: expected file '$n' not found" -ForegroundColor Yellow }
}
if (-not ($files | Where-Object { $_.DirectoryName -like '*\logs' })) { Write-Host "WARNING: no app log files found" -ForegroundColor Yellow }

if ($total -eq 0) {
    Write-Host "LEAK CHECK PASSED: 0 matches for $($pii.Count) PII values in $($files.Count) files." -ForegroundColor Green
} else {
    Write-Host "LEAK CHECK FAILED: $total match(es) found." -ForegroundColor Red
}
if ($selfExit -ne 0) { Write-Host "Self-test reported failures (exit code $selfExit) - see above." -ForegroundColor Yellow }
exit ([int]($total -ne 0))
