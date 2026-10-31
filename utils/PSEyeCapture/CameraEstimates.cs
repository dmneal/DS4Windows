// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;

namespace PSEyeCapture;

static class CameraEstimates
{
    public static bool HasCameraPosition(ServicePoseSample s) => s.Fresh && s.Optical && s.OpticalPositionValid &&
        s.TrackerId is >=0 and <32 && (s.TrackerMask&(1u<<s.TrackerId))!=0 && s.CameraPosition is {} p && p.Length()>.001f;
    public static Vector3 Degrees(Quaternion q) => new(
        MathF.Atan2(2*(q.W*q.Y+q.X*q.Z),1-2*(q.X*q.X+q.Y*q.Y))*180/MathF.PI,
        MathF.Asin(Math.Clamp(2*(q.W*q.X-q.Y*q.Z),-1,1))*180/MathF.PI,
        MathF.Atan2(2*(q.W*q.Z+q.X*q.Y),1-2*(q.X*q.X+q.Z*q.Z))*180/MathF.PI);
    static string Position(Vector3 p)=>$"X {p.X:F3}   Y {p.Y:F3}   Z {p.Z:F3} m";
    static string Orientation(Quaternion q) {
        var e=Degrees(q);
        return $"Yaw(Y) {e.X:F1}°  Pitch(X) {e.Y:F1}°  Roll(Z) {e.Z:F1}°\r\nXYZW: {q.X:F4}, {q.Y:F4}, {q.Z:F4}, {q.W:F4}";
    }
    public static string Describe(ServicePoseSample? s)
    {
        const string explanation="OPTICAL ESTIMATES — before IMU fusion\r\nDepth is inferred from known light-bar geometry, not measured by a depth sensor.\r\n\r\n";
        if(s is null || !s.Fresh) return explanation+"Unavailable / stale. Start native capture.\r\nNo fused pose is substituted.";
        bool position=HasCameraPosition(s);
        bool orientation=s.Optical && s.OpticalOrientationValid;
        string text=explanation+$"Tracker {s.TrackerId} · mask {s.TrackerMask} · sequence {s.Sequence}\r\n\r\nCAMERA-RELATIVE POSITION\r\n";
        text+=position && s.CameraPosition is {} p?$"{Position(p)}\r\nDepth (signed camera Z): {p.Z:F3} m\r\nRange from camera: {p.Length():F3} m\r\n":"Unavailable / rejected\r\n";
        text+="\r\nCAMERA-RELATIVE ORIENTATION\r\n"+(orientation && s.CameraOrientation is {} q?Orientation(q):"Unavailable / rejected");
        text+="\r\n\r\nSERVICE WORLD OPTICAL POSITION\r\n"+(s.Optical && s.OpticalPositionValid && s.OpticalPosition is {} world?Position(world):"Unavailable / rejected");
        text+="\r\n\r\nSERVICE WORLD OPTICAL ORIENTATION\r\n"+(orientation && s.OpticalOrientation is {} wq?Orientation(wq):"Unavailable / rejected");
        text+="\r\n\r\nLIGHT-BAR IMAGE CENTROID\r\n"+(position && s.CameraPixel is {} pixel?$"u {pixel.X:F1} px   v {pixel.Y:F1} px":"Unavailable / rejected");
        return text+"\r\n\r\nOrigin: light-bar center. Euler angles use Y/X/Z axes; quaternion order XYZW.\r\nPnP may use a previous-pose initial guess. Accepted estimates can still be noisy.";
    }
}
