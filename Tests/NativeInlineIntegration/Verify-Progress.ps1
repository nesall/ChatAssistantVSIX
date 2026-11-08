param([string]$Log = (Join-Path $PSScriptRoot 'bin/integration.jsonl'))
$ErrorActionPreference = 'Stop'
$all = @([IO.File]::ReadAllLines($Log) | ForEach-Object { $_ | ConvertFrom-Json })
$start = -1
for ($i = 0; $i -lt $all.Count; $i++) { if ($all[$i].event -eq 'baseline') { $start = $i } }
if ($start -lt 0) { throw 'No editor baseline recorded.' }
$events = @($all[$start..($all.Count - 1)])
$baseline = $events[0].buffer
$checks = 0
function Require([bool]$ok, [string]$message) { if (!$ok) { throw $message }; $script:checks++; Write-Output "PASS: $message" }
function Frame([string]$name, [int]$calls) { $events | Where-Object { $_.event -eq $name -and $_.calls -eq $calls } | Select-Object -Last 1 }
$samples = @($events | Where-Object { $_.event -eq 'inspect' -and $_.calls -eq 2 -and $_.progress })
Require ($samples.Count -ge 2 -and $samples[0].loading -and !$samples[0].proposal) 'Pending generation displayed a three-dot indicator before any proposal'
Require ($samples[0].dotOpacities.Count -eq 3 -and ($samples[0].dotOpacities -join ',') -ne ($samples[1].dotOpacities -join ',')) 'The three dots animated between real-editor samples'
$pending = $events | Where-Object { $_.event -eq 'released' -and $_.calls -eq 3 } | Select-Object -First 1
Require ($pending.loading -and $pending.progress -and !$pending.proposal) 'An obsolete response preserved the newer pending indicator'
$cancelled = Frame 'after-delayed' 3
Require (!$cancelled.loading -and !$cancelled.progress -and !$cancelled.proposal) 'Escape and a late cancelled response left no indicator or proposal'
$newer = Frame 'after-newer' 5
$late = Frame 'released' 5
Require ($newer.proposal -and !$newer.loading -and !$newer.progress) 'A newer native preview replaced the loading indicator'
Require ($late.proposal -and !$late.progress -and $late.proposalId -eq $newer.proposalId) 'An obsolete response preserved the newer native preview'
$clicked = Frame 'released' 6
Require (!$clicked.loading -and !$clicked.progress -and !$clicked.proposal -and $clicked.caret -eq $events[0].caret) 'Click cancellation preserved the caret and cleared the pending indicator'
$completed = Frame 'after-delayed' 7
Require ($completed.proposal -and !$completed.loading -and !$completed.progress) 'Successful delayed completion removed the indicator and displayed ghost text'
$errorFrame = Frame 'after-error' 8
Require (!$errorFrame.proposal -and !$errorFrame.loading -and !$errorFrame.progress) 'Backend failure left no indicator or proposal'
Require (@($events | Where-Object { $_.event -eq 'cancelled' -and $_.id -eq 9 }).Count -gt 0 -and @($events | Where-Object event -EQ 'closed').Count -gt 0) 'Closing the pending editor cancelled its request'
$changed = @($events | Where-Object { $_.PSObject.Properties['buffer'] -and $_.buffer -cne $baseline })
Require ($changed.Count -eq 0) 'All progress and preview checks left the document unchanged'
Require (@($events | Where-Object event -EQ 'ERROR').Count -eq 0) 'No integration-driver exception'
Write-Output "PASS: $checks progress assertions. Escape, pill click and document-close actions were observed in the UI run."
