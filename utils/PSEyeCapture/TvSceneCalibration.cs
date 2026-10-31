// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Text.Json;

namespace PSEyeCapture;

record TvSceneCalibration(TvModel Model, Vector3 ServiceReference, bool MeasurementOnly)
{
    public Vector3 ReferenceInTv => ToTvPosition(ServiceReference);
    public Vector3 ToTvPosition(Vector3 servicePosition) => Vector3.Transform(servicePosition-Model.Center,Quaternion.Inverse(Model.Rotation));
    public Quaternion ToTvOrientation(Quaternion serviceOrientation) => Quaternion.Normalize(Quaternion.Inverse(Model.Rotation)*serviceOrientation);
    public Vector3[] ScreenCorners => [new(-Model.Width/2,Model.Height/2,0),new(Model.Width/2,Model.Height/2,0),new(Model.Width/2,-Model.Height/2,0),new(-Model.Width/2,-Model.Height/2,0)];
    static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
    public static TvSceneCalibration Parse(string json)
    {
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        int version=root.GetProperty("Version").GetInt32();
        bool Flag(string key)=>root.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.True;
        bool measurement=version==3;
        if(version<1 || version>3 || (measurement?!(Flag("CalibrationComplete")&&Flag("MeasurementValidated")):!Flag("Validated")))
            throw new InvalidDataException("Saved TV calibration is incomplete or unsupported.");
        var options=new JsonSerializerOptions {IncludeFields=true};
        var model=root.GetProperty("Model").Deserialize<TvModel>(options) ?? throw new InvalidDataException("Saved TV model is missing.");
        float qLength=model.Rotation.LengthSquared();
        if(!float.IsFinite(model.Width)||!float.IsFinite(model.Height)||model.Width<.1f||model.Height<.1f||model.Width>10||model.Height>10||
            !Finite(model.Center)||model.Center.Length()>100||!float.IsFinite(qLength)||Math.Abs(qLength-1)>.02f||!Finite(model.LocalAim)||Math.Abs(model.LocalAim.LengthSquared()-1)>.02f)
            throw new InvalidDataException("Saved TV geometry contains invalid dimensions, position or rotation.");
        model=model with {Rotation=Quaternion.Normalize(model.Rotation)};
        Vector3 reference;
        if(root.TryGetProperty("FixedServiceRayOrigin",out var origin)&&origin.ValueKind==JsonValueKind.Object) reference=origin.Deserialize<Vector3>(options);
        else reference=model.Center+Vector3.Transform(new Vector3(0,0,1.5f),model.Rotation);
        if(!Finite(reference)||reference.Length()>100) throw new InvalidDataException("Saved reference position is invalid.");
        return new(model,reference,measurement);
    }
    public static TvSceneCalibration? Load(string path,out string status)
    {
        if(!File.Exists(path)) {status="No saved TV calibration - relative controller view";return null;}
        try {var result=Parse(File.ReadAllText(path));status=result.MeasurementOnly?"TV frame from tape measurements and reference attitude; live translation is a camera estimate":"TV frame from saved calibration";return result;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException) {
            status="TV calibration unavailable: "+ex.Message;return null;
        }
    }
}
