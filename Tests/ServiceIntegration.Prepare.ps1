$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'service/bin'
$sandbox = Join-Path $output 'sandbox'
New-Item -ItemType Directory -Force -Path $sandbox | Out-Null
$source = Join-Path $PSScriptRoot '../Utils/PhenixCodeCoreService.cs'
$text = [IO.File]::ReadAllText($source)
$needle = 'Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)'
if (($text.Split([string[]]@($needle), [StringSplitOptions]::None).Count - 1) -ne 1 -or
    $text -notmatch ('SettingsPath => Path.Combine\(' + [regex]::Escape($needle))) {
  throw 'Expected exactly one ApplicationData expression, in SettingsPath; refusing to generate.'
}
$replacement = 'Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sandbox")'
$generated = $text.Replace($needle, $replacement)
if ($generated.Replace($replacement, $needle) -cne $text) { throw 'Source transformation did not round trip.' }
$target = Join-Path $sandbox 'PhenixCodeCoreService.generated.cs'
[IO.File]::WriteAllText($target, $generated, [Text.UTF8Encoding]::new($false))
function Get-Sha256([string]$path) {
  $hash = [Security.Cryptography.SHA256]::Create()
  $stream = [IO.File]::OpenRead($path)
  try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
  finally { $stream.Dispose(); $hash.Dispose() }
}
@{
  source = [IO.Path]::GetFullPath($source)
  source_sha256 = Get-Sha256 $source
  generated_sha256 = Get-Sha256 $target
  replacement = $replacement
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $sandbox 'source-hash.json') -Encoding UTF8
