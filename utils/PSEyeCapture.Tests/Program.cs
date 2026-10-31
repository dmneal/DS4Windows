// SPDX-License-Identifier: GPL-3.0-or-later
using DS4Windows.OpticalTracking;
using PSEyeCapture;
using System.Buffers.Binary;
using System.Numerics;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
void Throws<T>(Action action, string name) where T:Exception {
    try { action(); } catch (T) { checks++; return; } throw new Exception(name);
}
const int width=64, height=48, stride=width*3+4;
byte[] frame = new byte[stride*height];
void Blob(int x,int y,int w=6,int h=4,byte b=255,byte g=0,byte r=255) {
    for(int yy=y;yy<y+h;yy++) for(int xx=x;xx<x+w;xx++) {
        int p=yy*stride+xx*3; frame[p]=b; frame[p+1]=g; frame[p+2]=r;
    }
}
var detector=new LightBarDetector(width,height);
Check(detector.Detect(frame,stride,100)==null,"black frame");
Blob(10,10);
var target=detector.Detect(frame,stride,110);
Check(target is {Area:24,Timestamp:110},"component area/timestamp");
Check(Math.Abs(target!.Value.X-12.5/63)<1e-10 && Math.Abs(target.Value.Y-11.5/47)<1e-10,"normalized centroid with padded stride");
Blob(45,30,8,4);
Check(detector.Detect(frame,stride,120) is {Area:24},"associated bar wins over larger distant distractor");
detector.Reset();
Check(detector.Detect(frame,stride,130)==null,"ambiguous acquisition rejected");
Array.Clear(frame); Blob(10,10,1,1);
Check(detector.Detect(frame,stride,140)==null,"noise rejected");
Array.Clear(frame); Blob(0,0,64,48);
Check(detector.Detect(frame,stride,150)==null,"large background rejected; flood fill bounded");
Array.Clear(frame); Blob(10,10,b:255,g:255,r:255);
Check(detector.Detect(frame,stride,160)==null,"white saturation rejected");
Array.Clear(frame); Blob(10,10,b:40,r:40);
Check(detector.Detect(frame,stride,170)==null,"dark purple rejected");
Array.Clear(frame); Blob(10,10,b:0,g:255,r:0);
Check(detector.Detect(frame,stride,180)==null,"non-purple rejected");
Array.Clear(frame); Blob(0,0);
Check(detector.Detect(frame,stride,190)!=null,"edge component");
Array.Clear(frame);
Check(detector.Detect(frame,stride,200)==null,"target loss");
Blob(50,35);
Check(detector.Detect(frame,stride,210)!=null,"unique target reacquired after loss");
Throws<ArgumentException>(()=>detector.Detect(new byte[3],stride,220),"invalid frame size");
detector.MinimumHue=double.NaN;
Throws<InvalidOperationException>(()=>detector.Detect(frame,stride,220),"invalid threshold");

var calibration = new OpticalCalibration(0.2,0.3,0.8,0.9);
var observation = new LightBarObservation(0.5,0.6,24,1000);
var point = calibration.Project(observation,1050,1000);
Check(point!=null && Math.Abs(point.Value.X-0.5)<1e-10 && Math.Abs(point.Value.Y-0.5)<1e-10,"region midpoint");
Check(calibration.Project(observation,1151,1000)==null,"stale capture rejected");
Check(calibration.Project(observation,999,1000)==null,"future capture rejected");
Check(calibration.Project(observation with {X=double.NaN},1050,1000)==null,"nonfinite coordinate rejected");
Check(calibration.Project(observation with {Area=0},1050,1000)==null,"invalid area rejected");
Check(calibration.Project(observation with {X=0,Y=1},1050,1000)==(0d,1d),"region clamped");
Check(new OpticalCalibration(0,0,1,1,true).Project(observation with {X=0.2},1050,1000)?.X==0.8,"mirror");
Check(default(OpticalCalibration).Project(observation,1050,1000)==null,"default calibration invalid");
Throws<ArgumentException>(()=>new OpticalCalibration(0.5,0,0.5,1),"degenerate calibration");
Throws<ArgumentException>(()=>new OpticalCalibration(0,0,double.NaN,1),"nonfinite calibration");

byte[] header=new byte[32];
void Header(int offset,int value)=>BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset),value);
Header(0,0x31455950); Header(4,1); Header(8,640); Header(12,480); Header(16,1920); Header(20,921600);
BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24),1000);
Check(FrameHeader.Parse(header) is {Width:640,Timestamp:1000},"native header");
Header(20,int.MaxValue); Throws<InvalidDataException>(()=>FrameHeader.Parse(header),"unbounded payload rejected");
Header(20,921600); Header(4,2); Throws<InvalidDataException>(()=>FrameHeader.Parse(header),"protocol version rejected");
Throws<InvalidDataException>(()=>FrameHeader.Parse(new byte[3]),"truncated header rejected");
Console.WriteLine($"PASS: {checks} optical detection, association, calibration, freshness and frame protocol checks");

var motion = new MotionAttitude();
uint seq = 0; ulong us = 0; long host = 0;
void Move(Vector3 rates,Vector3? acceleration=null) => motion.Update(new MotionSample(++seq,us+=10000,1,1,acceleration??new Vector3(0,-1,0),rates),host+=10000,1_000_000);
Move(Vector3.Zero);
for(int i=0;i<100;i++) Move(new Vector3(0,90,0));
var state=motion.Snapshot(host,1_000_000);
Check(state.Fresh && state.Connection==1,"USB attitude freshness");
var aim=Vector3.Transform(-Vector3.UnitZ,state.Orientation);
Check(aim.X>0.999 && Math.Abs(aim.Z)<0.001,"one-second right yaw integration");
Check(Math.Abs(state.Orientation.Length()-1)<1e-6,"orientation stays normalized");
motion.Update(new MotionSample(seq,us,1,1,new(0,-1,0),new(0,90,0)),host,1_000_000);
Check(motion.Snapshot(host,1_000_000).Samples==state.Samples,"duplicate motion packet ignored");
Check(!motion.Snapshot(host+300000,1_000_000).Fresh,"stale motion not shown as tracked");
host+=300000; Move(Vector3.Zero);
Check(Vector3.Distance(Vector3.Transform(-Vector3.UnitZ,motion.Snapshot(host,1_000_000).Orientation),-Vector3.UnitZ)<1e-5,"dropout rebaselines attitude");
motion.BeginBiasCalibration();
for(int i=0;i<110;i++) Move(new Vector3(0,1,0));
Check(motion.Snapshot(host,1_000_000).Calibration.Contains("measured"),"stationary bias completed");
motion.SetForward();
for(int i=0;i<100;i++) Move(new Vector3(0,1,0));
Check(Vector3.Distance(Vector3.Transform(-Vector3.UnitZ,motion.Snapshot(host,1_000_000).Orientation),-Vector3.UnitZ)<1e-5,"measured yaw bias removed");
var tilted=new MotionAttitude();
tilted.Update(new(1,10000,2,1,new(0,0,-1),Vector3.Zero),10000,1_000_000);
Check(Vector3.Distance(Vector3.Transform(new(0,0,-1),tilted.Snapshot(10000,1_000_000).Orientation),new(0,-1,0))<1e-5,"gravity initializes tilt");
var corners=PoseGeometry.Corners(Quaternion.Identity,Vector3.Zero);
Check(Math.Abs(corners.Max(v=>v.X)-corners.Min(v=>v.X)-0.162)<1e-6,"box width in meters");
Check(Math.Abs(corners.Max(v=>v.Y)-corners.Min(v=>v.Y)-0.052)<1e-6,"box height in meters");
Check(Math.Abs(corners.Max(v=>v.Z)-corners.Min(v=>v.Z)-0.098)<1e-6,"box depth in meters");
var rotated=PoseGeometry.Corners(Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2),new(1,2,3));
Check(Math.Abs(rotated.Max(v=>v.X)-rotated.Min(v=>v.X)-0.098)<1e-6 && Vector3.Distance(rotated.Aggregate(Vector3.Zero,(s,v)=>s+v)/8,new(1,2,3))<1e-6,"box rotation and translation preserve dimensions/center");
Check(MotionProtocol.Crc("123456789"u8)==0xcbf43926,"DSU CRC vector");
byte[] packet=new byte[100]; "DSUS"u8.CopyTo(packet);
BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4),1001); BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6),84);
BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16),0x100002); packet[21]=2; packet[23]=1; packet[31]=0; // DS4Windows 3.0.18 leaves activity at zero
BitConverter.GetBytes(-1f).CopyTo(packet,80); BitConverter.GetBytes(45f).CopyTo(packet,92);
BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8),MotionProtocol.Crc(packet));
Check(MotionProtocol.Parse(packet) is {Connection:1,PitchYawRoll.Y:45},"DSU axes and transport decode");
packet[92]^=1; Check(MotionProtocol.Parse(packet)==null,"corrupt motion packet rejected");
Check(MotionProtocol.Parse(new byte[50])==null,"truncated motion packet rejected");
const string nativeJson="""{"Sequence":7,"ControllerId":0,"OrientationValid":true,"PositionValid":true,"Optical":false,"TrackerMask":1,"Position":[0.1,-0.2,0.7],"Quaternion":[0,0,0,1]}""";
var native=ServicePoseSample.Parse(nativeJson);
Check(native.Position==new Vector3(.1f,-.2f,.7f) && native.Orientation==Quaternion.Identity,"native meters and XYZW quaternion survive bridge");
Check(native.OrientationValid && native.PositionValid && !native.Optical,"predicted pose is distinct from current optical observation");
Check(native.Fresh && !(native with {Received=System.Diagnostics.Stopwatch.GetTimestamp()-System.Diagnostics.Stopwatch.Frequency}).Fresh,"native receipt timeout rejects frozen stream");
Throws<InvalidDataException>(()=>ServicePoseSample.Parse(nativeJson.Replace("[0,0,0,1]","[0,0,0,0]")),"zero quaternion rejected");
Throws<InvalidDataException>(()=>ServicePoseSample.Parse(nativeJson.Replace("[0.1,-0.2,0.7]","[0.1]")),"malformed native position rejected");
Console.WriteLine($"PASS: {checks} total tracking, attitude, transport and 3D geometry checks");
var (tvw,tvh)=TvCalibration.Dimensions(55,16,9);
Check(Math.Abs(Math.Sqrt(tvw*tvw+tvh*tvh)-55*.0254)<1e-6,"TV diagonal converted to physical meters");
var truth=new TvModel(tvw,tvh,new(.03f,.36f,-.04f),Quaternion.CreateFromYawPitchRoll(.10f,-.03f,.02f),Vector3.Normalize(new(.10f,.08f,-1)));
Quaternion AimAt(Vector3 from,Vector2 uv,float roll=0) {
    var d=Vector3.Normalize(truth.TargetPoint(uv)-from); var a=truth.LocalAim;
    var cross=Vector3.Cross(a,d); var q=Quaternion.Normalize(new Quaternion(cross,1+Vector3.Dot(a,d)));
    return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(d,roll)*q);
}
var observations=new List<AimMeasurement>();
Vector3[] stations=[new(0,0,2),new(-.4f,.1f,1.8f),new(.4f,-.1f,2.3f)];
Vector2[] targets=[new(.08f,.08f),new(.92f,.08f),new(.92f,.92f),new(.08f,.92f)];
for(int station=0;station<3;station++) foreach(var uv in targets) observations.Add(new(stations[station],AimAt(stations[station],uv,.04f*station),uv,station));
var fitted=TvCalibration.Fit(tvw,tvh,observations);
Check(observations.Max(s=>TvCalibration.Error(fitted,s))<.002,"joint screen-plane and pointing-axis fit recovers synthetic aims");
var unseen=new AimMeasurement(new(.2f,.15f,1.6f),AimAt(new(.2f,.15f,1.6f),new(.36f,.62f)),new(.36f,.62f),3);
Check(TvCalibration.Error(fitted,unseen)<.003,"calibration generalizes to held-out position and interior target");
Throws<ArgumentException>(()=>TvCalibration.Fit(tvw,tvh,observations.Take(8).ToArray()),"insufficient calibration coverage rejected");
Throws<ArgumentException>(()=>TvCalibration.Fit(tvw,tvh,observations.Select(s=>s with {Position=Vector3.UnitZ}).ToArray()),"rotation-only calibration rejected");
var flat=new TvModel(1,1,Vector3.Zero,Quaternion.Identity,-Vector3.UnitZ);
Check(flat.Project(new(0,0,1),Quaternion.Identity)==new Vector2(.5f,.5f),"forward ray intersects center");
Check(flat.Project(new(0,0,-1),Quaternion.Identity)==null,"intersection behind ray rejected");
Check(flat.Project(new(0,0,1),Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2))==null,"parallel ray rejected");
Console.WriteLine($"PASS: {checks} total checks including TV calibration and held-out geometry");
var cameraSample=ServicePoseSample.Parse(nativeJson[..^1]+""", "OpticalPositionValid":true,"OpticalOrientationValid":true,"OpticalPosition":[1,2,3],"OpticalQuaternion":[0,0,0,1],"CameraPosition":[0.1,0.2,0.7],"CameraQuaternion":[0,0,0,1],"CameraPixel":[120,240],"TrackerId":0}""") with {Optical=true};
Check(cameraSample.OpticalPosition==new Vector3(1,2,3) && cameraSample.CameraPosition==new Vector3(.1f,.2f,.7f),"optical and camera positions remain distinct from fused position");
Check(cameraSample.CameraOrientation==Quaternion.Identity && cameraSample.CameraPixel==new Vector2(120,240),"camera quaternion and pixel centroid decode");
Check(CameraEstimates.HasCameraPosition(cameraSample) && !CameraEstimates.HasCameraPosition(cameraSample with {OpticalPositionValid=false}),"camera depth gated by optical position validity");
Check(!CameraEstimates.HasCameraPosition(cameraSample with {TrackerMask=0}),"missing camera observation rejects depth");
Check(CameraEstimates.Describe(cameraSample with {OpticalOrientationValid=false}).Contains("Unavailable / rejected"),"rejected optical orientation labelled");
Check(CameraEstimates.Describe(cameraSample with {Received=0}).Contains("Unavailable / stale"),"stale optical values hidden");
Check(Vector3.Distance(CameraEstimates.Degrees(Quaternion.CreateFromYawPitchRoll(.3f,.2f,.1f)),new Vector3(.3f,.2f,.1f)*180/MathF.PI)<.001,"optical Euler angle convention matches Y X Z rotation");
Console.WriteLine($"PASS: {checks} total checks including camera-only telemetry");

// Tape survey: geometric position independent of noisy camera depth.
var surveyWidth=1.7046f;var surveyHeight=.95884f;var surveyedPoint=new Vector3(.25f,-.8f,2f);
Vector3[] surveyCorners=[new(-surveyWidth/2,surveyHeight/2,0),new(surveyWidth/2,surveyHeight/2,0),new(-surveyWidth/2,-surveyHeight/2,0),new(surveyWidth/2,-surveyHeight/2,0)];
var tapeDistances=surveyCorners.Select(p=>(double)Vector3.Distance(p,surveyedPoint)).ToArray();
var located=TvSurvey.Locate(surveyWidth,surveyHeight,tapeDistances);
Check(Vector3.Distance(located.PositionMeters,surveyedPoint)<.00001f,"four tape distances recover off-center point below TV in meters");
var badTape=(double[])tapeDistances.Clone();badTape[0]+=.5;
Throws<ArgumentException>(()=>TvSurvey.Locate(surveyWidth,surveyHeight,badTape),"inconsistent corner lengths rejected");
Throws<ArgumentException>(()=>TvSurvey.Locate(surveyWidth,surveyHeight,[.1,.1,.1,.1]),"impossible tape geometry rejected");
Throws<ArgumentException>(()=>TvSurvey.Locate(surveyWidth,surveyHeight,[double.NaN,1,1,1]),"nonfinite tape lengths rejected");
long surveyStart=System.Diagnostics.Stopwatch.GetTimestamp();
var refRotation=Quaternion.CreateFromYawPitchRoll(.4f,.1f,-.05f);var surveyAnchor=new Vector3(.1f,-.2f,1.2f);
var surveySamples=Enumerable.Range(0,601).Select(i=> {
    float noise=.1f*MathF.Sin(i*MathF.PI/10);
    var q=i%2==0?refRotation:new Quaternion(-refRotation.X,-refRotation.Y,-refRotation.Z,-refRotation.W);
    return cameraSample with {Sequence=i,Received=surveyStart+(long)(i/60.0*System.Diagnostics.Stopwatch.Frequency),Position=surveyAnchor+new Vector3(0,0,noise),OpticalPosition=surveyAnchor+new Vector3(0,0,noise),Orientation=q,AccelerationG=Vector3.UnitY,PositionValid=true,OrientationValid=true,Optical=true,OpticalPositionValid=true};
}).ToArray();
var surveyAverage=TvSurvey.Average(surveySamples);
Check(surveyAverage.Samples==601 && surveyAverage.Seconds>=9.99,"survey averages a full ten-second recording");
Check(surveyAverage.OpticalPositionStdDev!.Value.Z>.06f && Vector3.Distance(surveyAverage.OpticalPositionMean!.Value,surveyAnchor)<.001,"large depth jitter is reported and averaged rather than resetting capture");
Check(Math.Abs(Quaternion.Dot(surveyAverage.OrientationMean,refRotation))>.99999f,"survey quaternion mean handles antipodal representations");
var surveyReference=TvSurvey.Reference(77,surveyWidth,surveyHeight,tapeDistances,surveyAverage);
Check(Vector3.Distance(surveyReference.Model.Center,surveyAnchor-Vector3.Transform(surveyedPoint,refRotation))<.001,"physical reference attitude and tape position establish TV frame alignment");
var requestedUv=new Vector2(.3f,.4f);var tvTarget=new Vector3((requestedUv.X-.5f)*surveyWidth,(.5f-requestedUv.Y)*surveyHeight,0);
var requestedDirection=Vector3.Normalize(tvTarget-surveyedPoint);var forward=-Vector3.UnitZ;
var localAimRotation=Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(forward,requestedDirection)),MathF.Acos(Vector3.Dot(forward,requestedDirection)));
var requestedWorldRotation=Quaternion.Normalize(refRotation*localAimRotation);
Check(surveyReference.Model.Project(surveyReference.FixedServiceRayOrigin,requestedWorldRotation) is {} surveyHit && Vector2.Distance(surveyHit,requestedUv)<.001,"tape reference maps known aiming direction to screen without camera depth updates");
Throws<InvalidOperationException>(()=>TvSurvey.Average(surveySamples.Take(100).ToArray()),"short survey recording rejected");
Throws<InvalidOperationException>(()=>TvSurvey.Average(surveySamples.Where((s,i)=>i<200 || i>240).ToArray()),"survey recording with stale-data gap rejected");
var driftingSurvey=surveySamples.Select((s,i)=>s with {Orientation=Quaternion.Normalize(refRotation*Quaternion.CreateFromAxisAngle(Vector3.UnitY,i/600f*.2f))}).ToArray();
Throws<InvalidOperationException>(()=>TvSurvey.Average(driftingSurvey),"moving or drifting reference rejected instead of averaging a wrong attitude");
Console.WriteLine($"PASS: {checks} total checks including tape-survey geometry and ten-second averaging");
var switchedController=surveySamples.Select((s,i)=>i>300?s with {ControllerId=s.ControllerId+1}:s).ToArray();
Throws<InvalidOperationException>(()=>TvSurvey.Average(switchedController),"controller changes invalidate the survey recording");
Throws<InvalidOperationException>(()=>TvSurvey.Reference(77,surveyWidth,surveyHeight,tapeDistances,surveyAverage with {OpticalSamples=0,OpticalPositionMean=null}),"missing optical anchor cannot silently become a TV alignment");
Console.WriteLine($"PASS: {checks} total checks including survey connection identity and optical anchor validity");

using(var measurementPayload=System.Text.Json.JsonDocument.Parse(TvSurvey.MeasurementCalibrationJson(surveyReference,"survey-session.json","DISPLAY1"))) {
    var root=measurementPayload.RootElement;
    Check(root.GetProperty("Version").GetInt32()==3 && root.GetProperty("CalibrationComplete").GetBoolean(),"measurement-only calibration is complete without target captures");
    Check(root.GetProperty("MeasurementValidated").GetBoolean() && !root.GetProperty("PointingValidated").GetBoolean(),"measurement validation is distinguished from untested pointing accuracy");
}
Console.WriteLine($"PASS: {checks} total checks including measurement-only calibration persistence");
