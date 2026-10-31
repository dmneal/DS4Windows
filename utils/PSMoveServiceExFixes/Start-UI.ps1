param([switch]$Original, [switch]$Tracking)
$ErrorActionPreference = 'Stop'
try {
    $serviceDirectory = Join-Path $PSScriptRoot 'service\PSMoveService'
    $dataRoot = Join-Path $PSScriptRoot 'data'
    $patchedDirectory = Join-Path $PSScriptRoot 'patched\PSMoveService'
    if (!$Original -and (Test-Path (Join-Path $patchedDirectory 'ds4-fixes-ready.txt'))) {
        $serviceDirectory = $patchedDirectory
        $dataRoot = Join-Path $PSScriptRoot 'patched-data'
    }
    $serviceExe = Join-Path $serviceDirectory 'PSMoveService.exe'
    $uiExe = Join-Path $serviceDirectory 'PSMoveConfigTool.exe'
    # Switch only between the two local installations owned by this launcher.
    $ownedServices = @((Join-Path $PSScriptRoot 'service\PSMoveService\PSMoveService.exe'),
                      (Join-Path $PSScriptRoot 'patched\PSMoveService\PSMoveService.exe'))
    foreach ($process in @(Get-Process PSMoveService -ErrorAction SilentlyContinue)) {
        if ($process.Path -ne $serviceExe) {
            if ($process.Path -notin $ownedServices) { throw 'Another PSMoveService installation is running. Close it before opening this one.' }
            $process.Kill()
            $process.WaitForExit()
        }
    }
    function Test-TrackingPort {
        $client = [Net.Sockets.TcpClient]::new()
        try {
            $pending = $client.ConnectAsync('127.0.0.1', 9512)
            return $pending.Wait(250) -and $client.Connected
        } catch { return $false }
        finally { $client.Dispose() }
    }
    if (!(Test-TrackingPort)) {
        $running = @(Get-Process PSMoveService -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) {
            $info = [Diagnostics.ProcessStartInfo]::new($serviceExe)
            $info.WorkingDirectory = $serviceDirectory
            $info.UseShellExecute = $false
            $info.CreateNoWindow = $true
            # Reuse the camera/color/controller configuration already calibrated here.
            $info.EnvironmentVariables['APPDATA'] = $dataRoot
            [Diagnostics.Process]::Start($info) | Out-Null
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (!(Test-TrackingPort)) {
            if ([DateTime]::UtcNow -ge $deadline) { throw 'PSMoveServiceEx did not open port 9512 within 15 seconds. Check camera/controller access and restart the service.' }
            Start-Sleep -Milliseconds 150
        }
    }
    if ($Tracking) {
        $workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
        $preview = Join-Path $workspace 'artifacts\PSEyeCapture\PSEyeCapture.exe'
        Start-Process -FilePath $preview -ArgumentList '--start' -WorkingDirectory (Split-Path -Parent $preview)
    } else {
        Start-Process -FilePath $uiExe -WorkingDirectory $serviceDirectory
    }
} catch {
    $_ | Out-String | Set-Content (Join-Path $PSScriptRoot 'launcher-error.log')
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'PSMoveServiceEx startup failed') | Out-Null
    exit 1
}
