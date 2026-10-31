// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net.Sockets;

namespace PSEyeCapture;

static class ServiceControl
{
    public static string Executable(string directory) => Path.GetFullPath(Path.Combine(directory, "PSMoveService.exe"));
    public static bool Running(string directory)
    {
        string executable;
        try { executable=Executable(directory); } catch(ArgumentException) {return false;} catch(NotSupportedException) {return false;}
        foreach (var process in Process.GetProcessesByName("PSMoveService"))
            using (process) {
                try { if (string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)) return true; }
                catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
        return false;
    }
    static async Task<bool> Listening()
    {
        using var client = new TcpClient();
        try { using var timeout = new CancellationTokenSource(300); await client.ConnectAsync("127.0.0.1", 9512, timeout.Token); return true; }
        catch (SocketException) { return false; } catch (OperationCanceledException) { return false; }
    }
    public static async Task Restart(string directory, Action<string> report)
    {
        string executable = Executable(directory);
        if (!File.Exists(executable)) throw new FileNotFoundException("Select the service folder containing PSMoveService.exe first.", executable);
        foreach (var process in Process.GetProcessesByName("PSMoveService"))
            using (process) {
                string? path;
                try { path = process.MainModule?.FileName; } catch (InvalidOperationException) { continue; }
                if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A different PSMoveService installation is running. Close it before restarting the selected installation.");
                report("Stopping selected service…");
                try { process.Kill(); await process.WaitForExitAsync(); } catch (InvalidOperationException) { }
            }
        await Task.Delay(1000); // Release USB/camera handles before reopening.
        string configuration = ServicePose.ConfigurationDirectory(directory);
        for (int attempt = 1; attempt <= 3; attempt++) {
            report($"Starting service (attempt {attempt}/3)…");
            var info = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true };
            if (Directory.Exists(configuration) && string.Equals(Path.GetFileName(configuration.TrimEnd(Path.DirectorySeparatorChar)), "PSMoveService", StringComparison.OrdinalIgnoreCase))
                info.Environment["APPDATA"] = Directory.GetParent(configuration)!.FullName;
            using var process = Process.Start(info) ?? throw new IOException("Could not start PSMoveService.");
            var elapsed = Stopwatch.StartNew(); double? readySince = null;
            while (elapsed.Elapsed.TotalSeconds < 20 && !process.HasExited) {
                if (await Listening()) readySince ??= elapsed.Elapsed.TotalSeconds;
                else readySince = null;
                if (readySince is double since && elapsed.Elapsed.TotalSeconds - since >= 3) {
                    report("Service connected; reconnecting tracking…"); return;
                }
                await Task.Delay(250);
            }
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
            await Task.Delay(1000);
        }
        throw new IOException("Service did not stay ready after three attempts. See PSMoveServiceEx.log in the selected service folder.");
    }
}
