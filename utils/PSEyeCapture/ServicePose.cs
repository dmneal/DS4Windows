// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace PSEyeCapture;

record ServicePoseSample(int Sequence, int ControllerId, bool OrientationValid, bool PositionValid,
    bool Optical, uint TrackerMask, Vector3 Position, Quaternion Orientation, long Received)
{
    public bool Fresh => Stopwatch.GetElapsedTime(Received).TotalMilliseconds < 250;
    public static ServicePoseSample Parse(string line)
    {
        using var doc=JsonDocument.Parse(line); var r=doc.RootElement;
        if(r.TryGetProperty("Reason",out var reason)) throw new InvalidDataException(reason.GetString());
        var p=r.GetProperty("Position").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        var q=r.GetProperty("Quaternion").EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        if(p.Length!=3 || q.Length!=4 || p.Concat(q).Any(x=>!float.IsFinite(x))) throw new InvalidDataException("Nonfinite or malformed pose");
        var orientation=new Quaternion(q[0],q[1],q[2],q[3]);
        if(orientation.LengthSquared()<.5f || orientation.LengthSquared()>1.5f) throw new InvalidDataException("Invalid quaternion norm");
        return new(r.GetProperty("Sequence").GetInt32(),r.GetProperty("ControllerId").GetInt32(),
            r.GetProperty("OrientationValid").GetBoolean(),r.GetProperty("PositionValid").GetBoolean(),
            r.GetProperty("Optical").GetBoolean(),r.GetProperty("TrackerMask").GetUInt32(),new(p[0],p[1],p[2]),Quaternion.Normalize(orientation),Stopwatch.GetTimestamp());
    }
}

sealed class ServicePose : IDisposable
{
    public ServicePoseSample? Sample => Volatile.Read(ref sample);
    public string Status => Volatile.Read(ref status);
    ServicePoseSample? sample;
    string status="Native pose stopped";
    Process? process;
    CancellationTokenSource? cancellation;
    public void Start(string directory)
    {
        Dispose(); sample=null; status="Connecting to PSMoveServiceEx…";
        cancellation=new(); var token=cancellation.Token;
        var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"pseye-capture.exe")) {
            WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        info.ArgumentList.Add("--pose-stream");
        try {
            process=Process.Start(info) ?? throw new IOException("Pose helper failed to start"); var child=process;
            var diagnostics=Task.Run(async()=> {
                try { while(await child.StandardError.ReadLineAsync(token) is {} line) if(!token.IsCancellationRequested) status=line; }
                catch(OperationCanceledException) { } catch(IOException) { }
            });
            var reader=Task.Run(async()=> {
                try {
                    int? sequence=null;
                    while(await child.StandardOutput.ReadLineAsync(token) is {} line) {
                        var next=ServicePoseSample.Parse(line);
                        if(sequence==next.Sequence) continue;
                        sequence=next.Sequence; if(!token.IsCancellationRequested) Volatile.Write(ref sample,next);
                    }
                    if(!token.IsCancellationRequested) status="Native pose stream ended — reconnect with Start / Apply";
                } catch(OperationCanceledException) { }
                catch(Exception ex) { if(!token.IsCancellationRequested) status="Native pose: "+ex.Message; }
            });
            _=Task.WhenAll(diagnostics,reader).ContinueWith(_=>child.Dispose(),TaskScheduler.Default);
        } catch(Exception ex) { status=ex.Message; }
    }
    public void Dispose()
    {
        cancellation?.Cancel();
        try { if(process is {HasExited:false}) process.Kill(); } catch(InvalidOperationException) { }
        // Reader tasks own the redirected streams until their cancellation completes.
        process=null; cancellation?.Dispose(); cancellation=null;
        sample=null; status="Native pose stopped";
    }
}
