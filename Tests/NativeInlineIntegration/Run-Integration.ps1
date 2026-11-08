param(
 [string]$VsRoot = 'C:/Program Files/Microsoft Visual Studio/18/Community',
 [string]$InstanceId = 'f80a46bd',
 [switch]$Visible
)
$ErrorActionPreference = 'Stop'
# A dedicated hive keeps the user's normal and Exp instances separate.
$suffix = 'PhenixInlineIntegration'
$build = Join-Path $VsRoot 'MSBuild/Current/Bin/amd64/MSBuild.exe'
$create = Join-Path $VsRoot 'VSSDK/VisualStudioIntegration/Tools/Bin/CreateExpInstance.exe'
$profileDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Microsoft/VisualStudio/18.0_$InstanceId$suffix"
if (!(Test-Path -LiteralPath $profileDirectory)) {
 & $create /Create "/VSInstance=18.0_$InstanceId" "/RootSuffix=$suffix"
 if ($LASTEXITCODE -ne 0) { throw 'Could not create the isolated Visual Studio profile.' }
}
& $build (Join-Path $PSScriptRoot 'NativeInlineIntegration.csproj') /t:Build "/p:ProbeVsRoot=$VsRoot" /p:DeployExtension=true "/p:DeployTargetInstanceId=$InstanceId" "/p:VSSDKTargetPlatformRegRootSuffix=$suffix" /p:UseSharedCompilation=false /nr:false /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Probe build/deployment failed.' }
$fixture = Join-Path $PSScriptRoot 'native-inline-integration.cpp'
$activityLog = Join-Path $PSScriptRoot 'bin/ActivityLog.xml'
$windowStyle = if ($Visible) { 'Normal' } else { 'Hidden' }
$process = Start-Process -FilePath (Join-Path $VsRoot 'Common7/IDE/devenv.exe') -ArgumentList @('/RootSuffix', $suffix, '/Log', ('"' + $activityLog + '"'), ('"' + $fixture + '"')) -WindowStyle $windowStyle -PassThru
Write-Output ("Started isolated editor process " + $process.Id + "; profile=" + $suffix)
Write-Output ("Open the disposable file in this instance: " + $fixture)
