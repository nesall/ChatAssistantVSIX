param([string]$Log = (Join-Path $PSScriptRoot 'bin/integration.jsonl'))
$ErrorActionPreference = 'Stop'
$events = @([IO.File]::ReadAllLines($Log) | ForEach-Object { $_ | ConvertFrom-Json })
$baseline = ($events | Where-Object event -EQ 'baseline' | Select-Object -Last 1).buffer
if (!$baseline) { throw 'No editor baseline recorded.' }
$checks = 0
function Require([bool]$ok, [string]$message) { if (!$ok) { throw $message }; $script:checks++; Write-Output "PASS: $message" }
$ready = @($events | Where-Object event -EQ 'after-immediate')
Require ($ready | Where-Object { $_.proposal -and !$_.loading -and $_.buffer -ceq $baseline }).Count 'Production controller displayed native ghost text without editing'
Require ($events | Where-Object { $_.event -eq 'after-multiline' -and $_.proposal -and $_.buffer -ceq $baseline }).Count 'Production multiline preview preserved the buffer'
Require ($events | Where-Object { $_.event -eq 'inspect' -and $_.buffer -ceq $baseline.Replace('std::puts();','std::puts("native integrated");') -and $_.selection -eq 0 }).Count 'Tab accepted the real proposal without selecting the insertion'
Require ($events | Where-Object { $_.event -eq 'explicit-accept' -and $_.loading -and !$_.proposal -and $_.buffer -ceq $baseline }).Count 'Accept while loading was a no-op'
Require ($events | Where-Object { $_.event -eq 'status' -and $_.message -like '*Generating inline suggestion*' }).Count 'Slow generation used status-bar feedback'
Require ($events | Where-Object { $_.event -eq 'cancelled' -and $_.id -eq 3 }).Count 'Escape cancelled the pending backend request'
$releases = @($events | Where-Object event -EQ 'released')
Require ($releases | Where-Object { $_.calls -eq 3 -and !$_.proposal -and !$_.loading -and $_.buffer -ceq $baseline }).Count 'Late result after Escape did not resurrect the preview'
$newer = $events | Where-Object event -EQ 'after-newer' | Select-Object -Last 1
Require ([bool]$newer.proposal) 'A newer request produced a native proposal'
Require ($releases | Where-Object { $_.calls -eq $newer.calls -and $_.proposalId -eq $newer.proposalId -and $_.proposal -and $_.buffer -ceq $baseline }).Count 'Old result neither replaced nor cleared the newer proposal'
Require ($events | Where-Object { $_.event -eq 'explicit-accept' -and !$_.loading -and !$_.proposal -and $_.buffer -ceq $baseline.Replace('std::puts();','std::puts("newer native suggestion");') -and $_.selection -eq 0 }).Count 'Existing explicit accept action used the native edit'
Require ($events | Where-Object { $_.event -eq 'after-error' -and !$_.proposal -and !$_.loading -and $_.buffer -ceq $baseline }).Count 'Failure recovered without leaving an active suggestion'
Require ($events | Where-Object { $_.event -eq 'status' -and $_.message -like '*Could not get*Fixture failure*' }).Count 'Backend failure was explained in the status bar'
Require ($releases | Where-Object { $_.calls -eq 8 -and !$_.proposal -and !$_.loading -and $_.caret -ne $newer.caret -and $_.buffer -ceq $baseline }).Count 'Moving the caret cancelled and rejected the old-position response'
Require ($events | Where-Object { $_.event -eq 'inspect' -and $_.buffer -ceq $baseline -and $_.selection -eq 0 }).Count 'Post-undo inspection restored the original buffer'
Require (($events | Where-Object event -EQ 'ERROR').Count -eq 0) 'No integration-driver exception'
Write-Output "PASS: $checks real-editor assertions. Undo keypress counts were observed during the UI run."
