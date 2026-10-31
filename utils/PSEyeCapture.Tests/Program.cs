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
Console.WriteLine($"PASS: {checks} total tracking, attitude, transport and 3D geometry checks");
