// SPDX-License-Identifier: GPL-3.0-or-later
using System;

namespace DS4Windows.OpticalTracking;

/// <summary>Axis-aligned camera region mapping; this is 2D calibration, not camera pose estimation.</summary>
internal readonly struct OpticalCalibration
{
    public double Left { get; }
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }
    public bool Mirror { get; }
    public OpticalCalibration(double left, double top, double right, double bottom, bool mirror = false)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            left < 0 || top < 0 || right > 1 || bottom > 1 || right - left < 0.01 || bottom - top < 0.01)
            throw new ArgumentException("Calibration requires a finite camera region at least 1% wide and high inside [0,1].");
        Left = left; Top = top; Right = right; Bottom = bottom; Mirror = mirror;
    }
    public (double X, double Y)? Project(LightBarObservation observation, long now, long frequency, double maximumAgeSeconds = 0.15)
    {
        if (frequency <= 0 || !double.IsFinite(maximumAgeSeconds) || maximumAgeSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));
        if (Right <= Left || Bottom <= Top || !double.IsFinite(observation.X) || !double.IsFinite(observation.Y) ||
            observation.X < 0 || observation.X > 1 || observation.Y < 0 || observation.Y > 1 || observation.Area <= 0 ||
            observation.Timestamp < 0 || observation.Timestamp > now || (now - observation.Timestamp) / (double)frequency > maximumAgeSeconds) return null;
        double x = Math.Clamp((observation.X - Left) / (Right - Left), 0, 1);
        return (Mirror ? 1 - x : x, Math.Clamp((observation.Y - Top) / (Bottom - Top), 0, 1));
    }
}
