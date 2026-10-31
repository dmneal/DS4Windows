param(
    [Parameter(Mandatory=$true)][string]$ZigExe,
    [Parameter(Mandatory=$true)][string]$PsmSdkRoot,
    [string]$ClientDll,
    [string]$OutputDirectory = "$PSScriptRoot\bin\native"
)
$ErrorActionPreference='Stop'
if (!(Test-Path "$PsmSdkRoot\include\PSMoveClient_CAPI.h") -or !(Test-Path "$PsmSdkRoot\bin\PSMoveClient_CAPI.dll")) {
    throw 'PsmSdkRoot must be the extracted PSMoveClient_CAPI directory from PSMoveServiceEx v0.27.8'
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
& $ZigExe c++ -target x86_64-windows-gnu -std=c++17 -O2 -I "$PsmSdkRoot\include" "$PSScriptRoot\native\capture.cpp" -o "$OutputDirectory\pseye-capture.exe"
if ($LASTEXITCODE -ne 0) { throw 'PSMoveServiceEx adapter compilation failed' }
if ([string]::IsNullOrWhiteSpace($ClientDll)) { $ClientDll = "$PsmSdkRoot\bin\PSMoveClient_CAPI.dll" }
Copy-Item -LiteralPath $ClientDll -Destination "$OutputDirectory\PSMoveClient_CAPI.dll"
Copy-Item -LiteralPath "$PsmSdkRoot\LICENSE.txt" -Destination "$OutputDirectory\PSMoveServiceEx-LICENSE.txt"
Copy-Item -LiteralPath "$PSScriptRoot\..\..\COPYING" -Destination "$OutputDirectory\DS4Windows-COPYING.txt"
Write-Output "Built PSMoveServiceEx adapter: $OutputDirectory\pseye-capture.exe"
