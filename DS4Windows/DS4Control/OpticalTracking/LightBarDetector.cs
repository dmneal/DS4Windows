// SPDX-License-Identifier: GPL-3.0-or-later
using System;

namespace DS4Windows.OpticalTracking;

internal readonly record struct LightBarObservation(double X, double Y, int Area, long Timestamp);

/// <summary>Single-target color-component detector. One instance belongs to one capture worker.</summary>
internal sealed class LightBarDetector
{
    private readonly int width, height;
    private readonly byte[] mask;
    private readonly int[] queue;
    private LightBarObservation? previous;
    public int MinimumArea { get; set; } = 15;
    public double MaximumAreaFraction { get; set; } = 0.2;
    public double MinimumHue { get; set; } = 250;
    public double MaximumHue { get; set; } = 350;
    public double MinimumSaturation { get; set; } = 0.30;
    public byte MinimumValue { get; set; } = 90;
    public double MaximumAssociationDistance { get; set; } = 0.20;

    public LightBarDetector(int width, int height)
    {
        if (width < 2 || height < 2 || width > 640 || height > 480)
            throw new ArgumentOutOfRangeException(nameof(width));
        this.width = width; this.height = height;
        mask = new byte[width * height]; queue = new int[mask.Length];
    }

    public void Reset() => previous = null;

    public LightBarObservation? Detect(ReadOnlySpan<byte> bgr, int stride, long timestamp)
    {
        if (stride < width * 3 || bgr.Length != checked(stride * height))
            throw new ArgumentException("Frame does not match the detector dimensions.");
        if (MinimumArea < 1 || !double.IsFinite(MaximumAreaFraction) || MaximumAreaFraction <= 0 || MaximumAreaFraction > 1 ||
            !double.IsFinite(MinimumHue) || !double.IsFinite(MaximumHue) || MinimumHue < 0 || MinimumHue > 360 || MaximumHue < 0 || MaximumHue > 360 ||
            !double.IsFinite(MinimumSaturation) || MinimumSaturation < 0 || MinimumSaturation > 1 ||
            !double.IsFinite(MaximumAssociationDistance) || MaximumAssociationDistance <= 0 || MaximumAssociationDistance > 2)
            throw new InvalidOperationException("Invalid light-bar detector settings.");
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = y * stride + x * 3;
                double b = bgr[p], g = bgr[p + 1], r = bgr[p + 2];
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
                double hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) :
                    max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
                bool hueMatches = MinimumHue <= MaximumHue ? hue >= MinimumHue && hue <= MaximumHue : hue >= MinimumHue || hue <= MaximumHue;
                mask[y * width + x] = (byte)(max >= MinimumValue && delta > 0 && delta / max >= MinimumSaturation && hueMatches ? 1 : 0);
            }
        LightBarObservation? best = null;
        double bestDistance = double.MaxValue;
        int eligible = 0;
        // Association lasts only across consecutive valid frames. Multiple candidates on acquisition are ambiguous.
        for (int seed = 0; seed < mask.Length; seed++)
        {
            if (mask[seed] == 0) continue;
            int head = 0, tail = 1; long sumX = 0, sumY = 0;
            queue[0] = seed; mask[seed] = 0;
            while (head < tail)
            {
                int p = queue[head++], x = p % width, y = p / width;
                sumX += x; sumY += y;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= width || (uint)ny >= height) continue;
                        int next = ny * width + nx;
                        if (mask[next] == 0) continue;
                        mask[next] = 0; queue[tail++] = next;
                    }
            }
            if (tail < MinimumArea || tail > mask.Length * MaximumAreaFraction) continue;
            var candidate = new LightBarObservation(sumX / (double)tail / (width - 1), sumY / (double)tail / (height - 1), tail, timestamp);
            double distance = previous is { } old ? Math.Sqrt(Math.Pow(candidate.X - old.X, 2) + Math.Pow(candidate.Y - old.Y, 2)) : 0;
            if (previous != null && distance > MaximumAssociationDistance) continue;
            eligible++;
            if (distance < bestDistance) { best = candidate; bestDistance = distance; }
        }
        // Reject ambiguous acquisition; nearest-neighbor association after acquisition.
        if (previous == null && eligible != 1) best = null;
        previous = best;
        return best;
    }
}
