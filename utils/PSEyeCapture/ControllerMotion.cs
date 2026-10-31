// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace PSEyeCapture;

internal readonly record struct MotionSample(uint Sequence, ulong Microseconds, ulong Device, byte Connection,
    Vector3 AccelerationG, Vector3 PitchYawRoll);

internal static class MotionProtocol
{
    public static uint Crc(ReadOnlySpan<byte> data) {
        uint crc = uint.MaxValue;
        foreach (byte b in data) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        return ~crc;
    }
    public static byte[] Subscribe(uint clientId) {
        var b = new byte[28]; "DSUC"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 1001);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), clientId);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 0x100002);
        b[20] = 1; // subscribe to slot 0, leaving DS4Windows the controller owner
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), Crc(b));
        return b;
    }
    public static MotionSample? Parse(ReadOnlySpan<byte> data) {
        if (data.Length != 100 || !data[..4].SequenceEqual("DSUS"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) != 1001 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) != 84 ||
            BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != 0x100002 ||
            data[20] != 0 || data[21] != 2) return null;
        Span<byte> copy = stackalloc byte[100]; data.CopyTo(copy); copy.Slice(8,4).Clear();
        if (Crc(copy) != BinaryPrimitives.ReadUInt32LittleEndian(data[8..])) return null;
        var a = new Vector3(BitConverter.ToSingle(data[76..]), BitConverter.ToSingle(data[80..]), BitConverter.ToSingle(data[84..]));
        var g = new Vector3(BitConverter.ToSingle(data[88..]), BitConverter.ToSingle(data[92..]), BitConverter.ToSingle(data[96..]));
        if (!Finite(a) || !Finite(g) || a.LengthSquared() > 256 || g.LengthSquared() > 48_000_000) return null;
        ulong device = 0; for (int i = 0; i < 6; i++) device |= (ulong)data[24+i] << (i*8);
        return new(BinaryPrimitives.ReadUInt32LittleEndian(data[32..]), BinaryPrimitives.ReadUInt64LittleEndian(data[68..]), device, data[23], a, g);
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

internal readonly record struct MotionSnapshot(Quaternion Orientation, bool Fresh, long Samples, byte Connection, string Calibration);

/// <summary>Diagnostic gravity-aided relative attitude, not optically anchored fusion or position tracking.</summary>
internal sealed class MotionAttitude
{
    readonly object sync = new();
    MotionSample? previous;
    Quaternion orientation = Quaternion.Identity;
    Vector3 bias, biasSum;
    bool calibrating;
    ulong biasStart;
    int biasCount;
    long lastHost, samples;
    string calibration = "Yaw is relative; use Set forward while pointing toward the TV.";
    public void SetForward() { lock (sync) { orientation = previous is {} p ? AlignGravity(p.AccelerationG) : Quaternion.Identity; calibration = "Forward reference set; yaw is not optically anchored."; } }
    public void BeginBiasCalibration() { lock (sync) { calibrating = true; biasStart = 0; biasCount = 0; biasSum = Vector3.Zero; calibration = "Keep the controller still for 1 second…"; } }
    public MotionSnapshot Snapshot(long now, long frequency) {
        lock (sync) return new(orientation, samples > 0 && now >= lastHost && (now-lastHost)/(double)frequency < 0.25,
            samples, previous?.Connection ?? 0, calibration);
    }
    public void Update(MotionSample p, long host, long frequency)
    {
        lock (sync)
        {
            bool disconnected = previous != null && (host-lastHost)/(double)frequency > 0.25;
            bool changed = previous is {} device && (device.Device != p.Device || device.Connection != p.Connection);
            if (!disconnected && !changed && previous is {} seq && unchecked((int)(p.Sequence-seq.Sequence)) <= 0) return;
            double dt = previous is {} old && p.Microseconds > old.Microseconds ? (p.Microseconds-old.Microseconds)/1_000_000.0 : 0;
            if (previous == null || disconnected || changed || dt <= 0 || dt > 0.1) {
                orientation = AlignGravity(p.AccelerationG);
                if (changed) bias = Vector3.Zero;
                if (previous != null) calibration = "Motion gap or device change: forward reference reset.";
                if (calibrating) { biasStart = 0; biasCount = 0; biasSum = Vector3.Zero; }
                previous = p; lastHost = host; samples++; return;
            }
            previous = p; lastHost = host; samples++;
            if (calibrating) {
                float gravity = p.AccelerationG.Length();
                if (p.PitchYawRoll.Length() > 3 || gravity < 0.85f || gravity > 1.15f) {
                    biasStart = 0; biasCount = 0; biasSum = Vector3.Zero;
                    calibration = "Movement detected: keep still to measure gyro bias.";
                } else {
                    if (biasStart == 0) biasStart = p.Microseconds;
                    biasSum += p.PitchYawRoll; biasCount++;
                    if (p.Microseconds-biasStart >= 1_000_000 && biasCount >= 50) {
                        bias = biasSum/biasCount; calibrating = false;
                        calibration = "Stationary gyro bias measured; yaw remains relative.";
                    }
                }
            }
            var rates = p.PitchYawRoll-bias;
            // Scene: +X right, +Y up, pointing forward -Z. DSU yaw/right maps to -Y.
            var omega = new Vector3(rates.X, -rates.Y, -rates.Z) * (MathF.PI/180);
            float norm = p.AccelerationG.Length();
            if (norm >= 0.85f && norm <= 1.15f) {
                var predictedDown = Vector3.Transform(-Vector3.UnitY, Quaternion.Conjugate(orientation));
                omega += Vector3.Cross(p.AccelerationG/norm, predictedDown)*0.8f;
            }
            float speed = omega.Length();
            if (speed > 1e-8) orientation = Quaternion.Normalize(orientation*Quaternion.CreateFromAxisAngle(omega/speed, speed*(float)dt));
        }
    }
    static Quaternion AlignGravity(Vector3 acceleration) {
        float norm = acceleration.Length();
        if (norm < 0.85f || norm > 1.15f) return Quaternion.Identity;
        var from = acceleration/norm; var to = -Vector3.UnitY;
        float dot = Math.Clamp(Vector3.Dot(from,to),-1,1);
        if (dot < -0.99999f) return Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI);
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from,to), 1+dot));
    }
}

internal sealed class ControllerMotion : IDisposable
{
    readonly UdpClient udp = new();
    readonly CancellationTokenSource stop = new();
    public MotionAttitude Attitude { get; } = new();
    public string? Error { get; private set; }
    public ControllerMotion() {
        udp.Connect(IPAddress.Loopback,26760);
        byte[] request = MotionProtocol.Subscribe((uint)Random.Shared.Next());
        _ = Task.Run(async () => {
            try { while (!stop.IsCancellationRequested) { await udp.SendAsync(request,stop.Token); await Task.Delay(1000,stop.Token); } }
            catch (OperationCanceledException) { } catch (Exception ex) { Error = ex.Message; }
        });
        _ = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                try { var packet = await udp.ReceiveAsync(stop.Token); var p = MotionProtocol.Parse(packet.Buffer);
                    if (p is {} sample) { Attitude.Update(sample,Stopwatch.GetTimestamp(),Stopwatch.Frequency); Error = null; } }
                catch (OperationCanceledException) { break; } catch (ObjectDisposedException) { break; }
                catch (SocketException ex) { Error = ex.Message; await Task.Delay(250); }
            }
        });
    }
    public void Dispose() { stop.Cancel(); udp.Dispose(); }
}
