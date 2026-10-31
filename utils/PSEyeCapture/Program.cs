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
        int directoryIndex = Array.IndexOf(args, "--service-dir");
        string serviceDirectory = directoryIndex >= 0 && directoryIndex + 1 < args.Length ? args[directoryIndex + 1] : FindServiceDirectory();
        if(args.Contains("--stationary-probe")) { ApplicationConfiguration.Initialize(); Environment.ExitCode=NativePoseProbe(serviceDirectory,true); return; }
        if(args.Contains("--native-pose-probe")) { ApplicationConfiguration.Initialize(); Environment.ExitCode=NativePoseProbe(serviceDirectory); return; }
        if (args.Contains("--pose-probe")) { ApplicationConfiguration.Initialize(); Environment.ExitCode = PoseProbe(); return; }
        if (args.Contains("--probe")) { Environment.ExitCode = LiveProbe(serviceDirectory).GetAwaiter().GetResult(); return; }
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTest().GetAwaiter().GetResult(); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new Preview(args.Contains("--synthetic"), serviceDirectory, !args.Contains("--no-start")));
    }
    static int PoseProbe()
    {
        using var motion = new ControllerMotion();
        MotionSnapshot snapshot = default;
        for (int i=0; i<250; i++) {
            snapshot = motion.Attitude.Snapshot(Stopwatch.GetTimestamp(),Stopwatch.Frequency);
            if (snapshot.Samples>=100) break;
            Thread.Sleep(20);
        }
        using var viewport = new PoseViewport { Size = new Size(1100,720), Orientation = snapshot.Orientation, Tracking = snapshot.Fresh,
            SourceText = snapshot.Fresh ? "Live USB/DSU IMU attitude · gravity-aided tilt · relative yaw" : "No motion telemetry: enable DS4Windows UDP 127.0.0.1:26760" };
        viewport.CreateControl();
        using var bitmap = new Bitmap(viewport.Width,viewport.Height);
        viewport.DrawToBitmap(bitmap,viewport.ClientRectangle);
        bitmap.Save(Path.Combine(AppContext.BaseDirectory,"pose-probe.png"),ImageFormat.Png);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"pose-probe.json"),System.Text.Json.JsonSerializer.Serialize(new {
            snapshot.Samples,snapshot.Fresh,snapshot.Connection,
            Quaternion = new { snapshot.Orientation.X,snapshot.Orientation.Y,snapshot.Orientation.Z,snapshot.Orientation.W },
            AbsolutePositionTracked=false, YawOpticallyAnchored=false, motion.Error
        },new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
        return snapshot.Fresh ? 0 : 1;
    }
    static int NativePoseProbe(string directory,bool stationaryCheck=false)
    {
        using var source=new ServicePose(); source.Start(directory);
        var samples=new List<ServicePoseSample>(); int? sequence=null;
        var started=Stopwatch.StartNew();
        while(started.Elapsed.TotalSeconds<(stationaryCheck?10:8) && samples.Count<(stationaryCheck?int.MaxValue:180)) {
            var s=source.Sample;
            if(s is not null && s.Sequence!=sequence) { samples.Add(s); sequence=s.Sequence; }
            Thread.Sleep(5);
        }
        var last=samples.LastOrDefault();
        bool accepted=last is {Fresh:true,OrientationValid:true,PositionValid:true};
        var center=samples.Count==0?System.Numerics.Vector3.Zero:samples.Aggregate(System.Numerics.Vector3.Zero,(sum,s)=>sum+s.Position)/samples.Count;
        double positionRms=samples.Count==0?0:Math.Sqrt(samples.Average(s=>System.Numerics.Vector3.DistanceSquared(s.Position,center)))*1000;
        double rotation=samples.Count<2?0:2*Math.Acos(Math.Clamp(Math.Abs(System.Numerics.Quaternion.Dot(samples[0].Orientation,samples[^1].Orientation)),0,1))*180/Math.PI;
        using var view=new PoseViewport {Size=new Size(1200,800)};
        if(last is not null) {
            view.Orientation=last.Orientation; view.Position=last.Position; view.NativeLightBarOrigin=true;
            view.Tracking=last.Fresh && last.OrientationValid; view.PositionTracked=last.Fresh && last.PositionValid;
            view.SourceText=$"LIVE PSMoveServiceEx fusion · sequence {last.Sequence} · optical {last.Optical} · tracker mask {last.TrackerMask}";
            view.PositionText=$"Native service position (meters): {last.Position.X:F3}, {last.Position.Y:F3}, {last.Position.Z:F3}";
        } else view.SourceText=source.Status;
        view.CreateControl(); using var bitmap=new Bitmap(view.Width,view.Height);
        view.DrawToBitmap(bitmap,view.ClientRectangle); bitmap.Save(Path.Combine(AppContext.BaseDirectory,"native-pose-probe.png"),ImageFormat.Png);
        var report=new {Samples=samples.Count,ValidPoses=samples.Count(s=>s.OrientationValid && s.PositionValid),OpticalSamples=samples.Count(s=>s.Optical),OpticalOrientationSamples=samples.Count(s=>s.OpticalOrientationValid),
            FreshAtCapture=accepted, PositionRmsMm=positionRms,EndToEndRotationDegrees=rotation,Last=last,SourceStatus=source.Status,Recording=stationaryCheck?samples:null};
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,stationaryCheck?"stationary-pose-check.json":"native-pose-probe.json"),System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
        return accepted?0:1;
    }
    static string FindServiceDirectory()
    {
        var processes = Process.GetProcessesByName("PSMoveService");
        try {
            if (processes.Length == 1) {
                string? folder = Path.GetDirectoryName(processes[0].MainModule?.FileName);
                if (folder != null && Directory.Exists(Path.Combine(folder, "shared_mem"))) return folder;
            }
        } catch (System.ComponentModel.Win32Exception) { } catch (InvalidOperationException) { }
        finally { foreach (var process in processes) process.Dispose(); }
        return Environment.CurrentDirectory;
    }
    static async Task<int> LiveProbe(string serviceDirectory)
    {
        var info = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "pseye-capture.exe")) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetFullPath(serviceDirectory)
        };
        using var process = Process.Start(info) ?? throw new IOException("Cannot start helper");
        var diagnostics = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        int frames = 0, visible = 0, code = 0;
        double fps = 0; string? error = null;
        LightBarObservation? lastTarget = null;
        try {
            var detector = new LightBarDetector(640, 480);
            var header = new byte[32]; var frame = new byte[640 * 480 * 3];
            long first = 0, last = 0;
            for (int i = 0; i < 120; i++) {
                await process.StandardOutput.BaseStream.ReadExactlyAsync(header, timeout.Token);
                var parsed = FrameHeader.Parse(header);
                await process.StandardOutput.BaseStream.ReadExactlyAsync(frame, timeout.Token);
                last = Stopwatch.GetTimestamp(); if (frames == 0) first = last;
                frames++;
                var target = detector.Detect(frame, parsed.Stride, parsed.Timestamp);
                if (target != null) { visible++; lastTarget = target; }
            }
            fps = (frames - 1) * (double)Stopwatch.Frequency / (last - first);
            using var bitmap = new Bitmap(640, 480, PixelFormat.Format24bppRgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, 640, 480), ImageLockMode.WriteOnly, bitmap.PixelFormat);
            try { for (int y = 0; y < 480; y++) Marshal.Copy(frame, y * 1920, data.Scan0 + y * data.Stride, 1920); }
            finally { bitmap.UnlockBits(data); }
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, "camera-probe.bmp"));
        } catch (Exception ex) { error = ex.Message; code = 1; }
        finally { if (!process.HasExited) process.Kill(); await process.WaitForExitAsync(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "camera-probe.log"), await diagnostics);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "camera-probe.json"), System.Text.Json.JsonSerializer.Serialize(new {
            Frames = frames, DeliveredFps = fps, VisibleSamples = visible, LastTarget = lastTarget, Error = error
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return code;
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
    readonly ServicePose nativePose = new();
    readonly CheckBox diagnosticMotion = new() { Text="Diagnostic DSU attitude only", AutoSize=true };
    readonly NumericUpDown displaySmoothing=new() {Minimum=0,Maximum=150,Value=35,Increment=5,Width=65};
    readonly List<ServicePoseSample> stationary=[];
    long stationaryStart,displayTime;
    int? displaySequence;
    bool measuring;
    readonly Label qualityStatus=new() {AutoSize=true,Text="Stationary check: hold controller still for 10 seconds."};
    readonly ControllerMotion motion = new();
    readonly PoseViewport pose = new() { Dock = DockStyle.Fill };
    readonly Label attitudeStatus = new() { AutoSize = true };
    readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly Label status = new() { AutoSize = true, Text = "Start capture to inspect the PS Eye. No mouse input is injected." };
    readonly NumericUpDown camera = new() { Minimum = 0, Maximum = 7, Value = 0, Width = 60 };
    readonly TextBox serviceFolder = new() { Width = 350 };
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
    public Preview(bool synthetic, string serviceDirectory, bool autoStart = false)
    {
        this.synthetic = synthetic;
        serviceFolder.Text = serviceDirectory;
        Text = "Controller calibration — 3D pose and PS Eye tracking"; Width = 1250; Height = 850;
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var start = new Button { Text = "Start / Apply", AutoSize = true };
        start.Click += async (_, _) => { start.Enabled = false; await StopCapture(); StartCapture(); start.Enabled = true; };
        var halt = new Button { Text = "Stop", AutoSize = true };
        halt.Click += async (_, _) => await StopCapture();
        var browse = new Button { Text = "Service folder…", AutoSize = true };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Select PSMoveServiceEx's working directory (containing shared_mem)", SelectedPath = serviceFolder.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) serviceFolder.Text = dialog.SelectedPath; };
        bar.Controls.AddRange([new Label { Text = "Service tracker index", AutoSize = true },camera,start,halt,
            browse,serviceFolder,new Label { Text = "Configure exposure, gain and frame rate in PSMoveServiceEx Config Tool.", AutoSize = true },status]);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var poseTab = new TabPage("1 · Controller in 3D");
        var cameraTab = new TabPage("Camera tracking");
        var poseTools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        var forward = new Button { Text = "Set forward", AutoSize = true };
        forward.Click += (_,_) => { if(diagnosticMotion.Checked) motion.Attitude.SetForward(); };
        var bias = new Button { Text = "Measure gyro bias (keep still)", AutoSize = true };
        bias.Click += (_,_) => { if(diagnosticMotion.Checked) motion.Attitude.BeginBiasCalibration(); };
        var resetView = new Button { Text = "Reset view", AutoSize = true };
        resetView.Click += (_,_) => pose.ResetView();
        forward.Enabled=bias.Enabled=false;
        diagnosticMotion.CheckedChanged += (_,_) => { forward.Enabled=bias.Enabled=diagnosticMotion.Checked; pose.ResetView(); };
        poseTools.Controls.AddRange([diagnosticMotion,forward,bias,resetView,attitudeStatus]);
        var measure=new Button {Text="Measure stationary drift / jitter (10 s)",AutoSize=true};
        measure.Click+=(_,_)=> {stationary.Clear(); stationaryStart=Stopwatch.GetTimestamp(); measuring=true;};
        poseTools.Controls.AddRange([new Label {Text="Display smoothing ms (raw pose used for calibration)",AutoSize=true},displaySmoothing,measure,qualityStatus]);
        poseTab.Controls.Add(pose); poseTab.Controls.Add(poseTools);
        cameraTab.Controls.Add(picture);
        tabs.TabPages.AddRange([poseTab,cameraTab]);
        var tvTab=new TabPage("TV calibration"); tvTab.Controls.Add(new TvCalibrationPanel(nativePose)); tabs.TabPages.Add(tvTab);
        var fusionTab=new TabPage("Fusion / tuning");
        var tuning=new TextBox { Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Font=new Font("Consolas",10) };
        var tuningTools=new FlowLayoutPanel { Dock=DockStyle.Top,AutoSize=true };
        var refreshSettings=new Button { Text="Read service settings",AutoSize=true };
        void ReadSettings() {
            string config=Path.GetFullPath(Path.Combine(serviceFolder.Text,"..","..","data","PSMoveService"));
            tuning.Text="POSE SOURCE: PSMoveServiceEx native C API → meters + quaternion XYZW → renderer\r\nNo second fusion filter is applied. DSU mode is an explicit diagnostic alternative.\r\n\r\nPIPELINE\r\nPS Eye color segmentation → DS4 light-bar triangle/quad fit → solvePnP → optical pose\r\nIMU → Madgwick ARG → quality-weighted optical quaternion correction\r\nOptical position → variance-based smoothing → optional IMU prediction\r\n\r\nVALIDITY\r\n250 ms receipt timeout; orientation, position and current optical visibility are separate.\r\nGray box = orientation stale/invalid. Predicted position is labelled when optical is lost.\r\nService sensor timestamp is not trusted; advancing network sequence gates receipt freshness.\r\nPosition labels use service coordinates; scene origin is the first tracked location. Reset view recenters.\r\nTV alignment and pointing-axis calibration are still pending.\r\n\r\nTUNING\r\nExposure/gain: keep bar colored, avoid white clipping; verify Camera tracking tab.\r\nHSV threshold: fit the real bar; avoid reflections. Current scene was H144±16 S160±80 V170±85.\r\nFrame rate: 60 Hz delivered ~59 fps; higher requests must be measured.\r\nPrediction time: reduce for overshoot, increase cautiously for latency.\r\nOptical smoothing: trades jitter against lag. Measure movement before changing.\r\nDS4 planar optical orientation is rejected beyond about ±30° yaw/pitch upstream.\r\nCamera intrinsics, gyro calibration and TV calibration are distinct procedures.\r\nUse upstream Config Tool for device calibration. Restart capture after closing its test stages.\r\n\r\nLOCAL PERSISTED CONFIG (not a live settings acknowledgement)\r\n"+config+"\r\n";
            if(Directory.Exists(config)) foreach(var file in Directory.GetFiles(config,"*.json").Where(f=>Path.GetFileName(f).StartsWith("dualshock4_") || (Path.GetFileName(f).StartsWith("PS3EyeTrackerConfig_") && !f.Contains("before"))))
                tuning.AppendText("\r\n"+Path.GetFileName(file)+"\r\n"+File.ReadAllText(file)+"\r\n");
            else tuning.AppendText("\r\nConfig directory unavailable. Use the Config Tool for this service instance.");
        }
        refreshSettings.Click+=(_,_)=>ReadSettings();
        var configTool=new Button {Text="Open upstream Config Tool",AutoSize=true};
        configTool.Click+=(_,_)=> {
            try { var info=new ProcessStartInfo(Path.Combine(serviceFolder.Text,"PSMoveConfigTool.exe")) {UseShellExecute=false,WorkingDirectory=serviceFolder.Text}; Process.Start(info); }
            catch(Exception ex) { status.Text=ex.Message; }
        };
        tuningTools.Controls.AddRange([refreshSettings,configTool]); fusionTab.Controls.Add(tuning); fusionTab.Controls.Add(tuningTools); tabs.TabPages.Add(fusionTab);
        ReadSettings();
        Controls.Add(tabs); Controls.Add(bar);
        timer.Tick += (_, _) => RefreshPreview(); timer.Start();
        if (autoStart) Shown += (_, _) => StartCapture();
        FormClosing += (_, _) => { stop?.Cancel(); KillHelper(); nativePose.Dispose(); };
        FormClosed += (_, _) => { timer.Dispose(); picture.Image?.Dispose(); motion.Dispose(); };
    }
    void StartCapture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "pseye-capture.exe");
        if (!File.Exists(path)) { status.Text = "Build/copy pseye-capture.exe and PSMoveClient_CAPI.dll beside this app first."; return; }
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!Directory.Exists(serviceFolder.Text)) { status.Text = "Select PSMoveServiceEx's working directory (containing shared_mem)."; return; }
        info.WorkingDirectory = Path.GetFullPath(serviceFolder.Text);
        if(!synthetic) nativePose.Start(info.WorkingDirectory);
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
        nativePose.Dispose();
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
        if(!diagnosticMotion.Checked) {
            pose.NativeLightBarOrigin=true;
            var sample=nativePose.Sample;
            bool fresh=sample?.Fresh==true;
            pose.Tracking=fresh && sample!.OrientationValid;
            pose.PositionTracked=fresh && sample!.PositionValid;
            if(sample is not null && displaySequence!=sample.Sequence) {
                float alpha=displaySmoothing.Value==0 || displayTime==0 || !fresh?1:1-MathF.Exp(-(float)Stopwatch.GetElapsedTime(displayTime).TotalMilliseconds/(float)displaySmoothing.Value);
                if(pose.Tracking) pose.Orientation=System.Numerics.Quaternion.Slerp(pose.Orientation,sample.Orientation,alpha);
                if(pose.PositionTracked) pose.Position=System.Numerics.Vector3.Lerp(pose.Position,sample.Position,alpha);
                displayTime=Stopwatch.GetTimestamp(); displaySequence=sample.Sequence;
            }
            pose.SourceText=sample is null?nativePose.Status:
                $"PSMoveServiceEx native fusion · DS4 {sample.ControllerId} · sequence {sample.Sequence} · received {Stopwatch.GetElapsedTime(sample.Received).TotalMilliseconds:F0} ms ago\nOrientation: {(fresh && sample.OrientationValid?"valid":"stale / invalid")} · position: {(fresh && sample.PositionValid?"valid":"stale / invalid")} · optical position: {(fresh && sample.Optical?"visible":"LOST")} · optical orientation: {(fresh && sample.OpticalOrientationValid?"accepted":"REJECTED / no absolute correction")}";
            if(measuring) {
                if(sample is {Fresh:true} && (stationary.Count==0 || stationary[^1].Sequence!=sample.Sequence)) stationary.Add(sample);
                double elapsed=Stopwatch.GetElapsedTime(stationaryStart).TotalSeconds;
                qualityStatus.Text=$"Keep still: {Math.Min(elapsed,10):F1}/10 s · {stationary.Count} samples";
                if(elapsed>=10) {
                    measuring=false;
                    if(stationary.Count<50 || stationary.Any(s=>!s.PositionValid || !s.OrientationValid)) qualityStatus.Text="Insufficient valid data; expose the bar and repeat.";
                    else {
                        var center=stationary.Aggregate(System.Numerics.Vector3.Zero,(sum,s)=>sum+s.Position)/stationary.Count;
                        double jitter=Math.Sqrt(stationary.Average(s=>System.Numerics.Vector3.DistanceSquared(s.Position,center)))*1000;
                        double rotation=2*Math.Acos(Math.Clamp(Math.Abs(System.Numerics.Quaternion.Dot(stationary[0].Orientation,stationary[^1].Orientation)),0,1))*180/Math.PI;
                        double fraction=stationary.Count(s=>s.OpticalOrientationValid)/(double)stationary.Count;
                        qualityStatus.Text=$"Raw pose: {jitter:F1} mm position RMS · {rotation:F2}° end-to-end rotation · optical orientation {fraction:P0}. Physical motion affects this test.";
                        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"stationary-pose-check.json"),System.Text.Json.JsonSerializer.Serialize(new {PositionRmsMm=jitter,EndToEndRotationDegrees=rotation,OpticalOrientationFraction=fraction,Samples=stationary},new System.Text.Json.JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
                    }
                }
            }
            pose.PositionText=pose.PositionTracked?$"Service position (m): X {sample!.Position.X:F3}  Y {sample.Position.Y:F3}  Z {sample.Position.Z:F3} · {(sample.Optical?"optically corrected":"PREDICTED — optical lost")}":"Absolute position unavailable / stale — display anchor only";
            attitudeStatus.Text="Native fusion — tune/calibrate in Fusion / tuning";
            pose.AccessibleName=pose.SourceText+" · "+pose.PositionText;
            pose.Invalidate(); return;
        }
        var attitude = motion.Attitude.Snapshot(Stopwatch.GetTimestamp(),Stopwatch.Frequency);
        pose.NativeLightBarOrigin=false; pose.PositionTracked=false; pose.PositionText="Diagnostic IMU only — absolute position UNTRACKED";
        pose.Orientation = attitude.Orientation; pose.Tracking = attitude.Fresh;
        string connection = attitude.Connection == 1 ? "USB wired" : attitude.Connection == 2 ? "Bluetooth (not validated)" : "unknown connection";
        pose.SourceText = attitude.Fresh ? $"Live {connection} IMU attitude · gravity-aided tilt · relative yaw\nOptical orientation fusion and absolute 3D position are not connected yet." :
            "Motion unavailable / stale. Enable DS4Windows UDP server at 127.0.0.1:26760; controller slot 1.";
        attitudeStatus.Text = attitude.Calibration;
        pose.Invalidate();
    }
}
