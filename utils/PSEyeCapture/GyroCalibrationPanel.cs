// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace PSEyeCapture;

sealed class GyroCalibrationPanel : UserControl
{
    readonly Func<string> serviceDirectory;
    readonly CheckBox enabled = new() { Text = "Learn gyro bias automatically while stationary", AutoSize = true };
    readonly CheckBox optical = new() { Text = "Also require stable camera orientation (optional; jitter can block learning)", AutoSize = true };
    readonly Dictionary<string, NumericUpDown> fields = new();
    readonly Label notice = new() { AutoSize = true };
    readonly TextBox status = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 11) };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    string SettingsPath => Path.Combine(serviceDirectory(), "gyro-autocalibration.json");

    public GyroCalibrationPanel(Func<string> serviceDirectory)
    {
        this.serviceDirectory = serviceDirectory;
        Dock = DockStyle.Fill;
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        tools.Controls.Add(new Label { AutoSize = true, Text = "Bias learning runs inside the patched service, before orientation fusion.\nUses gyro + accelerometer stillness for at least five seconds. Camera gating is optional.\nPausing learning keeps the current calibrated bias. Manual calibration takes precedence." });
        tools.Controls.Add(enabled); tools.Controls.Add(optical);
        void Add(string key, string text, decimal value, decimal min, decimal max, int decimals)
        {
            var row = new FlowLayoutPanel { AutoSize = true };
            var number = new NumericUpDown { Value = value, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = (decimal)Math.Pow(10, -decimals), Width = 85 };
            fields[key] = number;
            row.Controls.AddRange([new Label { Text = text, Width = 330, AutoSize = false, Height = 25 }, number]);
            tools.Controls.Add(row);
        }
        Add("windowSeconds", "Stillness window (seconds)", 5, 5, 30, 0);
        Add("gyroLimitDps", "Maximum mean residual gyro speed (degrees/s)", .08m, .02m, 1, 2);
        Add("gyroStdDps", "Maximum per-axis gyro standard deviation", .25m, .01m, .5m, 2);
        Add("accelStdG", "Maximum per-axis acceleration standard deviation (g)", .015m, .001m, .05m, 3);
        Add("opticalAngleDegrees", "Camera mean orientation change limit (degrees)", 1.25m, .1m, 2, 2);
        Add("opticalPositionMeters", "Camera mean position change limit (meters)", .01m, .002m, .03m, 3);
        Add("maxStepDps", "Maximum bias adjustment per window (degrees/s)", .01m, .001m, .02m, 3);
        Add("maxSessionCorrectionDps", "Maximum correction from session baseline (degrees/s)", .25m, .02m, .5m, 2);
        var buttons = new FlowLayoutPanel { AutoSize = true };
        var apply = new Button { Text = "Apply to service", AutoSize = true };
        var reload = new Button { Text = "Reload settings", AutoSize = true };
        apply.Click += (_, _) => {
            try {
                var values = fields.ToDictionary(p => p.Key, p => (object)p.Value.Value);
                values["enabled"] = enabled.Checked;
                values["requireOptical"] = optical.Checked;
                string path = SettingsPath;
                File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(path + ".tmp", path, true);
                notice.Text = "Saved. Live status below acknowledges service activity; settings reload within one second.";
            } catch (Exception ex) { notice.Text = ex.Message; }
        };
        reload.Click += (_, _) => LoadSettings();
        buttons.Controls.AddRange([apply, reload]); tools.Controls.Add(buttons); tools.Controls.Add(notice);
        Controls.Add(status); Controls.Add(tools);
        timer.Tick += (_, _) => RefreshStatus(); timer.Start();
        LoadSettings(); RefreshStatus();
    }

    void LoadSettings()
    {
        try {
            if (!File.Exists(SettingsPath)) { notice.Text = "No settings file: learning is disabled by default."; return; }
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            var root = doc.RootElement;
            enabled.Checked = root.TryGetProperty("enabled", out var e) && e.GetBoolean();
            optical.Checked = root.TryGetProperty("requireOptical", out var o) && o.GetBoolean();
            foreach (var pair in fields)
                if (root.TryGetProperty(pair.Key, out var v)) pair.Value.Value = Math.Clamp(v.GetDecimal(), pair.Value.Minimum, pair.Value.Maximum);
            notice.Text = "Loaded settings. Looser tolerances can mistake slow aiming for bias.";
        } catch (Exception ex) { notice.Text = "Could not read settings: " + ex.Message; }
    }

    void RefreshStatus()
    {
        try {
            var files = Directory.GetFiles(serviceDirectory(), "gyro-autocalibration-status-*.json");
            if (files.Length == 0) { status.Text = "Waiting for calibration-capable service data.\r\nConnect the DS4 and start native tracking. This requires the updated patched service."; return; }
            var lines = new List<string>();
            foreach (string path in files.Order()) {
                double age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                string Get(string key) => root.TryGetProperty(key, out var value) ? value.ToString() : "unavailable";
                lines.Add($"Controller {Get("controllerId")} — {(age > 3 ? "STALE / disconnected" : "live")} ({age:F1}s old)");
                lines.Add($"Learning enabled: {Get("enabled")}\r\nStillness source: {Get("stillnessSource")}\r\nStatus: {Get("reason")}");
                lines.Add($"Stillness window: {Get("windowProgressSeconds")} / {Get("windowSeconds")} s");
                lines.Add($"Last window reset: {Get("lastResetReason")} · longest window: {Get("longestWindowSeconds")} s");
                lines.Add($"Accepted windows: {Get("acceptedWindows")} · seconds since update: {Get("secondsSinceUpdate")} (-1 = none)");
                lines.Add("Current bias (degrees/s): " + Get("biasDps"));
                lines.Add(Get("savePolicy") + "\r\n");
            }
            status.Text = string.Join("\r\n", lines);
        } catch (Exception ex) { status.Text = "Calibration status unavailable: " + ex.Message; }
    }

    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
