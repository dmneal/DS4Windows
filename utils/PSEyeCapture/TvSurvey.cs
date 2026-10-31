// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;

namespace PSEyeCapture;

record TvSurveyPosition(Vector3 PositionMeters, double RmsDistanceErrorMeters, double MaxDistanceErrorMeters);
record TvSurveyAverage(int Samples, double Seconds, Vector3 ServicePositionMean, Vector3 ServicePositionStdDev,
    Quaternion OrientationMean, double AngularRmsDegrees, double EndWindowRotationDegrees,
    int OpticalSamples, Vector3? OpticalPositionMean, Vector3? OpticalPositionStdDev,
    Vector3? AccelerationMeanG);
record TvSurveyReference(float DiagonalInches, float WidthMeters, float HeightMeters, double[] CornerDistancesMeters,
    TvSurveyPosition Survey, TvSurveyAverage Tracking, TvModel Model, Vector3 FixedServiceRayOrigin,
    string Placement="Controller flat, buttons up, light bar toward TV normal; controller left/right axis parallel to screen bottom");

static class TvSurvey
{
    public static string MeasurementCalibrationJson(TvSurveyReference reference,string surveySession,string display) =>
        System.Text.Json.JsonSerializer.Serialize(new {
            Version=3,CalibrationComplete=true,MeasurementValidated=true,PointingValidated=false,
            Method="TapeSurveyFixedOrigin",ValidationMethod="TapeDistanceConsistencyAndReferenceStability",
            CreatedUtc=DateTime.UtcNow,Display=display,Reference=reference,Model=reference.Model,
            FixedServiceRayOrigin=reference.FixedServiceRayOrigin,SurveySession=surveySession,
            Basis="Four tape distances plus confirmed flat, straight-facing controller attitude and ten-second tracking average",
            Scope="Reference location; screen pointing accuracy and translation to other locations are untested",
            Invalidation="Moving camera/TV, changing tracker frame or controller, or changing the reference placement requires recalibration"
        },new System.Text.Json.JsonSerializerOptions {WriteIndented=true,IncludeFields=true});
    // Corners: top-left, top-right, bottom-left, bottom-right as seen by viewer.
    // TV frame: screen center origin; right +X, up +Y, toward viewer +Z.
    public static TvSurveyPosition Locate(float width, float height, IReadOnlyList<double> distances, double toleranceMeters=.015)
    {
        if(!float.IsFinite(width) || !float.IsFinite(height) || width<=0 || height<=0 ||
            distances.Count!=4 || distances.Any(d=>!double.IsFinite(d) || d<=0 || d>20) || !double.IsFinite(toleranceMeters) || toleranceMeters<=0)
            throw new ArgumentException("Enter four positive straight-line distances and valid visible screen dimensions.");
        var d=distances.Select(v=>v*v).ToArray();
        double x=((d[0]-d[1])+(d[2]-d[3]))/(4*width);
        double y=((d[2]-d[0])+(d[3]-d[1]))/(4*height);
        double z2=d.Average()-x*x-y*y-(width*width+height*height)/4;
        if(!double.IsFinite(z2) || z2<.0025) throw new ArgumentException("These measurements do not locate a point at least 5 cm in front of the screen. Check corner order, units and tape endpoint.");
        var position=new Vector3((float)x,(float)y,(float)Math.Sqrt(z2));
        Vector3[] corners=[new(-width/2,height/2,0),new(width/2,height/2,0),new(-width/2,-height/2,0),new(width/2,-height/2,0)];
        var errors=corners.Select((p,i)=>Math.Abs(Vector3.Distance(position,p)-distances[i])).ToArray();
        double worst=errors.Max(),rms=Math.Sqrt(errors.Average(e=>e*e));
        if(worst>toleranceMeters) throw new ArgumentException($"Corner distances disagree by up to {worst*100:F1} cm (limit {toleranceMeters*100:F1} cm). Remeasure from the same light-bar point.");
        return new(position,rms,worst);
    }
    static Vector3 Mean(IReadOnlyList<Vector3> points) => points.Aggregate(Vector3.Zero,(sum,p)=>sum+p)/points.Count;
    static Vector3 Std(IReadOnlyList<Vector3> points,Vector3 mean)
    {
        var variance=points.Aggregate(Vector3.Zero,(sum,p)=>sum+(p-mean)*(p-mean))/points.Count;
        return new(MathF.Sqrt(variance.X),MathF.Sqrt(variance.Y),MathF.Sqrt(variance.Z));
    }
    static Quaternion RotationMean(IReadOnlyList<Quaternion> rotations)
    {
        var reference=Quaternion.Normalize(rotations[0]);var sum=Vector4.Zero;
        foreach(var rotation in rotations) {var q=Quaternion.Normalize(rotation);sum+=new Vector4(q.X,q.Y,q.Z,q.W)*(Quaternion.Dot(reference,q)<0?-1:1);}
        if(sum.LengthSquared()<1e-8f) throw new InvalidOperationException("Orientation varied too much to average.");
        return Quaternion.Normalize(new(sum.X,sum.Y,sum.Z,sum.W));
    }
    static double Angle(Quaternion a,Quaternion b)=>2*Math.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a,b)),0,1))*180/Math.PI;
    public static TvSurveyAverage Average(IReadOnlyList<ServicePoseSample> samples)
    {
        if(samples.Count<200) throw new InvalidOperationException("Fewer than 200 fresh orientation samples; repeat the ten-second recording.");
        if(samples.Select(s=>s.ControllerId).Distinct().Count()!=1 || samples.Zip(samples.Skip(1),(a,b)=>b.Sequence<=a.Sequence).Any(b=>b))
            throw new InvalidOperationException("Controller or stream changed during recording; repeat with one uninterrupted connection.");
        double duration=System.Diagnostics.Stopwatch.GetElapsedTime(samples[0].Received,samples[^1].Received).TotalSeconds;
        if(duration<9.5 || samples.Zip(samples.Skip(1),(a,b)=>System.Diagnostics.Stopwatch.GetElapsedTime(a.Received,b.Received).TotalSeconds).Any(g=>g<=0 || g>.3))
            throw new InvalidOperationException("Tracking had a gap or less than ten seconds of coverage; repeat the recording.");
        var rotations=samples.Select(s=>s.Orientation).ToArray();
        if(rotations.Any(q=>!float.IsFinite(q.LengthSquared()) || q.LengthSquared()<.5f)) throw new InvalidOperationException("Invalid orientation sample.");
        var orientation=RotationMean(rotations);
        double angularRms=Math.Sqrt(rotations.Average(q=>Math.Pow(Angle(Quaternion.Normalize(q),orientation),2)));
        int quarter=samples.Count/4;
        double rotationChange=Angle(RotationMean(rotations.Take(quarter).ToArray()),RotationMean(rotations.TakeLast(quarter).ToArray()));
        if(angularRms>2 || rotationChange>2) throw new InvalidOperationException($"Controller moved or orientation drifted: RMS {angularRms:F2}°, first/last change {rotationChange:F2}°. Leave it supported and retry.");
        var positions=samples.Where(s=>s.PositionValid && s.Optical && float.IsFinite(s.Position.LengthSquared())).Select(s=>s.Position).ToArray();
        var position=positions.Length>0?Mean(positions):Vector3.Zero;
        var optical=samples.Where(s=>s.Optical && s.OpticalPositionValid && s.OpticalPosition is {} p && float.IsFinite(p.LengthSquared())).Select(s=>s.OpticalPosition!.Value).ToArray();
        var acceleration=samples.Where(s=>s.AccelerationG is {} a && float.IsFinite(a.LengthSquared())).Select(s=>s.AccelerationG!.Value).ToArray();
        var opticalMean=optical.Length>0?Mean(optical):(Vector3?)null;
        return new(samples.Count,duration,position,positions.Length>0?Std(positions,position):Vector3.Zero,orientation,angularRms,rotationChange,
            optical.Length,opticalMean,opticalMean is {} m?Std(optical,m):null,acceleration.Length>0?Mean(acceleration):null);
    }
    public static TvSurveyReference Reference(float diagonal,float width,float height,double[] distances,TvSurveyAverage tracking)
    {
        var survey=Locate(width,height,distances);
        if(tracking.OpticalSamples<200 || tracking.OpticalSamples<tracking.Samples/2 || tracking.OpticalPositionMean is not {} anchor)
            throw new InvalidOperationException("Need at least 200 optical position samples covering half the recording to anchor the surveyed point. Keep the light bar visible and retry.");
        // At the instructed reference attitude, controller axes define TV axes.
        // This is a user-supplied orientation constraint, not inferred from distances.
        var rotation=tracking.OrientationMean;
        var center=anchor-Vector3.Transform(survey.PositionMeters,rotation);
        return new(diagonal,width,height,distances,survey,tracking,new(width,height,center,rotation,-Vector3.UnitZ),anchor);
    }
}
