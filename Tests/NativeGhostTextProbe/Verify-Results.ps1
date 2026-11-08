param([string]$EventLog = (Join-Path $PSScriptRoot 'bin/events.log'))
$ErrorActionPreference = 'Stop'
$lines = [IO.File]::ReadAllLines($EventLog)
$frames = @($lines | Where-Object { $_ -match 'After display;|Accepted:|Dismissed:|Updated:|Inspect;' } | ForEach-Object {
 $parts = $_ -split '; buffer-base64=', 2
 [pscustomobject]@{ Event = $parts[0]; Buffer = if ($parts.Length -eq 2) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($parts[1])) } else { $null } }
})
function Require([bool]$condition, [string]$message) { if (!$condition) { throw $message }; Write-Output "PASS: $message" }
$previews = @($frames | Where-Object Event -Match 'After display;')
$baseline = $previews[0].Buffer
Require ($lines -match 'Attached; native service=True').Count 'Public native suggestion service was imported'
Require ($lines -match 'Registered=True').Count 'Provider registered with Visual Studio'
Require ($lines -match 'Displayed=True; buffer unchanged=True; multiline=False').Count 'Single-line native preview displayed without an edit'
Require ($lines -match 'Displayed=True; buffer unchanged=True; multiline=True').Count 'Multiline native preview displayed without an edit'
Require (($previews | Where-Object Buffer -CNE $baseline).Count -eq 0) 'All previews left the original buffer intact'
$accepted = @($frames | Where-Object Event -Match 'Accepted: AcceptedByCommand;')
Require ($accepted | Where-Object { $_.Buffer -ceq $baseline.Replace('std::puts();', 'std::puts("PhenixCode native ghost text");') }).Count 'Tab committed exactly the single-line proposal'
$newline = if ($baseline.Contains("`r`n")) { "`r`n" } else { "`n" }
$multiline = 'std::puts("PhenixCode native ghost text");' + $newline + '    std::puts("Second ghost line");'
Require ($accepted | Where-Object { $_.Buffer -ceq $baseline.Replace('std::puts();', $multiline) }).Count 'Tab committed the multiline proposal with document line endings and retained the suffix'
Require (($accepted | Where-Object Event -NotMatch '; selection=0$').Count -eq 0) 'Acceptance left no selection'
Require ($frames | Where-Object { $_.Event -match 'DismissedAfterUserEscape; proposal=False;' -and $_.Buffer -ceq $baseline }).Count 'Escape dismissed without modifying the buffer'
Require ($frames | Where-Object Event -Match 'DismissedAfterCaretMoved; proposal=False;').Count 'Moving the caret dismissed the native proposal'
Require ($frames | Where-Object Event -Match 'DivergedAfterTypeChar; proposal=False;').Count 'Diverging typing removed the native proposal'
Require ($frames | Where-Object { $_.Event -match 'Inspect;' -and $_.Buffer -ceq $baseline }).Count 'Post-undo inspection confirmed the original buffer'
Require ($frames[-1].Buffer -ceq $baseline) 'Final editor buffer was restored'
Require (($lines -match ' ERROR ').Count -eq 0) 'No probe runtime exceptions were logged'
