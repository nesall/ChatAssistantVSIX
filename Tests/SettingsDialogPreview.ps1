param([ValidateSet('All','Light','Dark','HighContrast')][string]$Theme = 'All')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot 'preview/bin'
$generated = Join-Path $PSScriptRoot 'preview/obj'
New-Item -ItemType Directory -Force $out,$generated | Out-Null
$xaml = [IO.File]::ReadAllText((Join-Path $root 'Dialogs/SettingsDialog.xaml'))
$xaml = $xaml -replace ';assembly=Microsoft.VisualStudio.Shell.15.0','' -replace ';assembly=Community.VisualStudio.Toolkit',''
# Keep the production resource keys and styles; replace only the unavailable VS dictionary.
$xaml = $xaml.Replace('<ResourceDictionary Source="{x:Static toolkit:ToolkitResourceKeys.ThemeResourcesUri}"/>', '<toolkit:PreviewThemeResources/>')
if ($xaml.Contains('ToolkitResourceKeys.ThemeResourcesUri')) { throw 'Preview theme dictionary substitution failed.' }
[IO.File]::WriteAllText((Join-Path $generated 'SettingsDialog.xaml'), $xaml)
$chatXaml = [IO.File]::ReadAllText((Join-Path $root 'ToolWindows/MyToolWindowControl.xaml'))
$chatXaml = $chatXaml -replace ';assembly=Microsoft.VisualStudio.Shell.15.0','' -replace ';assembly=Community.VisualStudio.Toolkit',''
$chatXaml = $chatXaml.Replace('<ResourceDictionary Source="{x:Static toolkit:ToolkitResourceKeys.ThemeResourcesUri}"/>', '<toolkit:PreviewThemeResources/>')
[IO.File]::WriteAllText((Join-Path $generated 'MyToolWindowControl.xaml'), $chatXaml)
$statsXaml = [IO.File]::ReadAllText((Join-Path $root 'Dialogs/StatsDialog.xaml'))
$statsXaml = $statsXaml -replace ';assembly=Microsoft.VisualStudio.Shell.15.0','' -replace ';assembly=Community.VisualStudio.Toolkit',''
$statsXaml = $statsXaml.Replace('<ResourceDictionary Source="{x:Static toolkit:ToolkitResourceKeys.ThemeResourcesUri}"/>', '<toolkit:PreviewThemeResources/>')
[IO.File]::WriteAllText((Join-Path $generated 'StatsDialog.xaml'), $statsXaml)
Copy-Item (Join-Path $root 'Resources/settings.default.json') (Join-Path $out 'fixture-settings.json')
Copy-Item (Join-Path $root 'Resources/providers.default.json') (Join-Path $out 'fixture-providers.json')
Get-FileHash (Join-Path $root 'Dialogs/SettingsDialog.xaml'),(Join-Path $root 'Dialogs/SettingsDialog.xaml.cs') | Format-List | Out-File (Join-Path $out 'source-hashes.txt')
$build = Get-Command msbuild -ErrorAction SilentlyContinue
if ($build) { $build = $build.Source }
else {
 $build = 'C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/amd64/MSBuild.exe'
 if (!(Test-Path $build)) {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
  $build = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
 }
}
if (!$build) { throw 'MSBuild was not found.' }
& $build (Join-Path $PSScriptRoot 'SettingsDialogPreview.csproj') /t:Build /p:UseSharedCompilation=false /nr:false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Preview build failed.' }
$previousTheme = $env:PHENIXCODE_PREVIEW_THEME
try {
 $palettes = if ($Theme -eq 'All') { @('Light','Dark','HighContrast') } else { @($Theme) }
 foreach ($palette in $palettes) {
  $env:PHENIXCODE_PREVIEW_THEME = $palette
  & (Join-Path $out 'SettingsDialogPreview.exe')
  if ($LASTEXITCODE -ne 0) { throw "Preview validation failed: $palette." }
  $paletteOut = Join-Path $out $palette
  New-Item -ItemType Directory -Force $paletteOut | Out-Null
  Copy-Item (Join-Path $out '*x*-*.png') $paletteOut
  Copy-Item (Join-Path $out 'layout-report.txt') $paletteOut
 }
}
finally { $env:PHENIXCODE_PREVIEW_THEME = $previousTheme }
