// SPDX-License-Identifier: GPL-3.0-or-later
using DS4Windows.OpticalTracking;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PSEyeCapture;

static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTest().GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new Preview(args.Contains("--synthetic")));
    }
    static async Task<int> SelfTest()
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "pseye-capture.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add("--synthetic");
        using var process = Process.Start(info) ?? throw new IOException("Cannot start helper");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string result;
        int code;
        try {
            var detector = new LightBarDetector(640, 480);
            var header = new byte[32]; var frame = new byte[640 * 480 * 3];
            var calibration = new OpticalCalibration(0, 0, 1, 1);
            for (int i = 0; i < 8; i++) {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(header, timeout.Token);
                var parsed = FrameHeader.Parse(header);
                await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, timeout.Token);
                var target = detector.Detect(frame, parsed.Stride, parsed.Timestamp);
                var projected = target is {} t ? calibration.Project(t, Stopwatch.GetTimestamp(), Stopwatch.Frequency) : null;
                if (target?.Area != 1200 || projected == null || Math.Abs(projected.Value.X - 0.5) > 0.001 || Math.Abs(projected.Value.Y - 0.5) > 0.001)
                    throw new InvalidDataException("Synthetic native frame did not produce the expected target");
            }
            result = "PASS: 8 synthetic frames through native helper, binary pipe, detector and freshness/calibration. No camera or mouse input used.";
            code = 0;
        } catch (Exception ex) { result = "FAIL: " + ex; code = 1; }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), result);
        return code;
    }
}

sealed class Preview : Form
{
    readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly Label status = new() { AutoSize = true, Text = "Start capture to inspect the PS Eye. No mouse input is injected." };
    readonly NumericUpDown camera = new() { Minimum = 0, Maximum = 7, Value = 0, Width = 60 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly object sync = new();
    readonly byte[] displayFrame = new byte[640 * 480 * 3];
    readonly OpticalCalibration calibration = new(0, 0, 1, 1);
    CancellationTokenSource? stop;
    Process? helper;
    Task? reader;
    string message = "Idle";
    LightBarObservation? target;
    long latest, started, frames;
    bool dirty;
    readonly bool synthetic;
    public Preview(bool synthetic)
    {
        this.synthetic = synthetic;
        Text = "PSMoveServiceEx PS Eye diagnostic — no mouse output"; Width = 850; Height = 650;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var start = new Button { Text = "Start / Apply", AutoSize = true };
        start.Click += async (_, _) => { start.Enabled = false; await StopCapture(); StartCapture(); start.Enabled = true; };
        var halt = new Button { Text = "Stop", AutoSize = true };
        halt.Click += async (_, _) => await StopCapture();
        bar.Controls.AddRange([new Label { Text = "Service tracker index", AutoSize = true },camera,start,halt,
            new Label { Text = "Configure exposure, gain and frame rate in PSMoveServiceEx Config Tool.", AutoSize = true },status]);
        Controls.Add(picture); Controls.Add(bar);
        timer.Tick += (_, _) => RefreshPreview(); timer.Start();
        FormClosing += (_, _) => { stop?.Cancel(); KillHelper(); };
        FormClosed += (_, _) => { timer.Dispose(); picture.Image?.Dispose(); };
    }
    void StartCapture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "pseye-capture.exe");
        if (!File.Exists(path)) { status.Text = "Build/copy pseye-capture.exe and libusb-1.0.dll beside this app first."; return; }
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "--camera", camera.Value.ToString() }) info.ArgumentList.Add(arg);
        if (synthetic) info.ArgumentList.Add("--synthetic");
        stop = new(); var token = stop.Token;
        try
        {
            helper = Process.Start(info) ?? throw new IOException("Failed to start capture helper");
            var process = helper;
            lock (sync) { target = null; latest = 0; frames = 0; dirty = false; message = "Starting camera…"; started = Stopwatch.GetTimestamp(); }
            var diagnostics = Task.Run(async () => {
                try {
                    while (await process.StandardError.ReadLineAsync(token) is { } line)
                        lock (sync) message = line.Length > 240 ? line[..240] : line;
                } catch (OperationCanceledException) { } catch (IOException) { }
            });
            reader = Task.Run(async () => {
                try
                {
                    var header = new byte[32]; var frame = new byte[640 * 480 * 3];
                    var detector = new LightBarDetector(640, 480);
                    while (!token.IsCancellationRequested)
                    {
                        await process.StandardOutput.BaseStream.ReadExactlyAsync(header, token);
                        var h = FrameHeader.Parse(header);
                        await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, token);
                        var observation = detector.Detect(frame, h.Stride, h.Timestamp);
                        lock (sync) { Buffer.BlockCopy(frame, 0, displayFrame, 0, frame.Length); target = observation; latest = h.Timestamp; frames++; dirty = true; }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) {
                    try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
                    await diagnostics;
                    lock (sync) { target = null; message = $"{message} | {ex.Message}"; }
                }
            });
        }
        catch (Exception ex) { status.Text = ex.Message; }
    }
    void KillHelper() { try { if (helper is { HasExited: false }) helper.Kill(); } catch (InvalidOperationException) { } }
    async Task StopCapture()
    {
        stop?.Cancel(); KillHelper();
        if (reader != null) await reader;
        helper?.Dispose(); helper = null; reader = null; stop?.Dispose(); stop = null;
        lock (sync) { target = null; latest = 0; message = "Stopped"; dirty = false; }
    }
    void RefreshPreview()
    {
        Bitmap? bitmap = null;
        lock (sync)
        {
            long now = Stopwatch.GetTimestamp();
            var point = target is { } t ? calibration.Project(t, now, Stopwatch.Frequency) : null;
            double age = latest == 0 ? double.NaN : (now - latest) * 1000.0 / Stopwatch.Frequency;
            if (helper is { HasExited: false } && Stopwatch.GetElapsedTime(latest == 0 ? started : latest).TotalSeconds > 3)
            { KillHelper(); target = null; message = "Capture timeout: helper stopped. Verify driver/camera and retry."; }
            status.Text = $"{message}\n{frames / Math.Max(0.001, Stopwatch.GetElapsedTime(started).TotalSeconds):F1} delivered fps | dequeue age {age:F0} ms | " +
                (point is { } p ? $"purple ({p.X:F3}, {p.Y:F3})" : "target absent / stale / ambiguous");
            if (dirty || latest != 0)
            {
                bitmap = new Bitmap(640, 480, PixelFormat.Format24bppRgb);
                var data = bitmap.LockBits(new Rectangle(0, 0, 640, 480), ImageLockMode.WriteOnly, bitmap.PixelFormat);
                try { for (int y = 0; y < 480; y++) Marshal.Copy(displayFrame, y * 640 * 3, data.Scan0 + y * data.Stride, 640 * 3); }
                finally { bitmap.UnlockBits(data); }
                if (point is { } xy) { using var g = Graphics.FromImage(bitmap); using var pen = new Pen(Color.Lime, 2); g.DrawEllipse(pen, (float)(xy.X * 639) - 10, (float)(xy.Y * 479) - 10, 20, 20); }
                dirty = false;
            }
        }
        if (bitmap != null) { var old = picture.Image; picture.Image = bitmap; old?.Dispose(); }
    }
}
