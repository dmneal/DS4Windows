// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace PSEyeCapture;

record ServicePoseSample(int Sequence, int ControllerId, bool OrientationValid, bool PositionValid,
    bool Optical, uint TrackerMask, Vector3 Position, Quaternion Orientation, long Received)
{
    public bool CrossDown {get;init;}
    public bool OpticalOrientationValid {get;init;}
    public bool OpticalPositionValid {get;init;}
    public Vector3? AccelerationG {get;init;}
    public Vector3? OpticalPosition {get;init;}
    public Quaternion? OpticalOrientation {get;init;}
    public Vector3? CameraPosition {get;init;}
    public Quaternion? CameraOrientation {get;init;}
    public Vector2? CameraPixel {get;init;}
    public int TrackerId {get;init;}=-1;
    static float[]? Numbers(JsonElement r,string name,int count) {
        if(!r.TryGetProperty(name,out var a) || a.ValueKind!=JsonValueKind.Array) return null;
        var values=a.EnumerateArray().Select(x=>x.GetSingle()).ToArray();
        return values.Length==count && values.All(float.IsFinite)?values:null;
    }
    static Vector3? Vector(JsonElement r,string name) => Numbers(r,name,3) is {} a?new(a[0],a[1],a[2]):null;
    static Quaternion? Rotation(JsonElement r,string name) {
        if(Numbers(r,name,4) is not {} a) return null;
        var q=new Quaternion(a[0],a[1],a[2],a[3]);
        return q.LengthSquared() is >.5f and <1.5f?Quaternion.Normalize(q):null;
    }
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
            r.GetProperty("Optical").GetBoolean(),r.GetProperty("TrackerMask").GetUInt32(),new(p[0],p[1],p[2]),Quaternion.Normalize(orientation),Stopwatch.GetTimestamp()) {
                CrossDown=r.TryGetProperty("CrossDown",out var cross) && cross.GetBoolean(),
                OpticalOrientationValid=r.TryGetProperty("OpticalOrientationValid",out var oo) && oo.GetBoolean(),
                OpticalPositionValid=r.TryGetProperty("OpticalPositionValid",out var op) && op.GetBoolean(),
                AccelerationG=Vector(r,"AccelG"), OpticalPosition=Vector(r,"OpticalPosition"), OpticalOrientation=Rotation(r,"OpticalQuaternion"),
                CameraPosition=Vector(r,"CameraPosition"), CameraOrientation=Rotation(r,"CameraQuaternion"),
                CameraPixel=Numbers(r,"CameraPixel",2) is {} pixel?new(pixel[0],pixel[1]):null,
                TrackerId=r.TryGetProperty("TrackerId",out var tracker)?tracker.GetInt32():-1 };
    }
}

sealed class ServicePose : IDisposable
{
    public static string ConfigurationDirectory(string serviceDirectory) {
        string marker=Path.Combine(serviceDirectory,"configuration-path.txt");
        return File.Exists(marker)?Path.GetFullPath(File.ReadAllText(marker).Trim()):Path.GetFullPath(Path.Combine(serviceDirectory,"..","..","data","PSMoveService"));
    }
    public string Directory {get;private set;}=Environment.CurrentDirectory;
    public ServicePoseSample? Sample => Volatile.Read(ref sample);
    public string Status => Volatile.Read(ref status);
    ServicePoseSample? sample;
    string status="Native pose stopped";
    Process? process;
    CancellationTokenSource? cancellation;
    public void Start(string directory)
    {
        Directory=directory;
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
