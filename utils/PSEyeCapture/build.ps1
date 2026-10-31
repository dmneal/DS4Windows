param(
    [string]$DotnetExe = 'dotnet',
    [Parameter(Mandatory=$true)][string]$ZigExe,
    [Parameter(Mandatory=$true)][string]$PsmSdkRoot,
    [string]$ClientDll,
    [string]$OutputDirectory = "$PSScriptRoot\bin\package"
)
$ErrorActionPreference='Stop'
$config = "$PSScriptRoot\NuGet.Config"
$tests = "$PSScriptRoot\..\PSEyeCapture.Tests\PSEyeCapture.Tests.csproj"
& $DotnetExe restore $tests --configfile $config
if ($LASTEXITCODE -ne 0) { throw 'Test restore failed' }
& $DotnetExe run --project $tests --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tracking tests failed' }
& "$PSScriptRoot\build-native.ps1" -ZigExe $ZigExe -PsmSdkRoot $PsmSdkRoot -ClientDll $ClientDll -OutputDirectory "$PSScriptRoot\bin\native"
& $DotnetExe restore "$PSScriptRoot\PSEyeCapture.csproj" --configfile $config
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic restore failed' }
& $DotnetExe publish "$PSScriptRoot\PSEyeCapture.csproj" --no-restore -c Release -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed' }
Copy-Item "$PSScriptRoot\bin\native\pseye-capture.exe","$PSScriptRoot\bin\native\PSMoveClient_CAPI.dll","$PSScriptRoot\bin\native\PSMoveServiceEx-LICENSE.txt","$PSScriptRoot\bin\native\DS4Windows-COPYING.txt" $OutputDirectory
Copy-Item "$PSScriptRoot\README.md","$PSScriptRoot\THIRD_PARTY_NOTICES.md" $OutputDirectory
$testProcess = Start-Process "$OutputDirectory\PSEyeCapture.exe" -ArgumentList '--self-test' -WindowStyle Hidden -PassThru
if (!$testProcess.WaitForExit(15000)) { $testProcess.Kill(); throw 'Synthetic pipeline test timed out' }
Get-Content "$OutputDirectory\self-test.txt"
if ($testProcess.ExitCode -ne 0) { throw 'Synthetic pipeline test failed' }
