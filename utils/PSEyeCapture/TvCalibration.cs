// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;

namespace PSEyeCapture;

record AimMeasurement(Vector3 Position, Quaternion Orientation, Vector2 Target, int Station);
record TvModel(float Width, float Height, Vector3 Center, Quaternion Rotation, Vector3 LocalAim)
{
    public Vector3 TargetPoint(Vector2 uv) => Center+Vector3.Transform(new((uv.X-.5f)*Width,(.5f-uv.Y)*Height,0),Rotation);
    public Vector2? Project(Vector3 position, Quaternion orientation)
    {
        var direction=Vector3.Transform(LocalAim,orientation);
        var normal=Vector3.Transform(Vector3.UnitZ,Rotation);
        float denominator=Vector3.Dot(normal,direction);
        if(Math.Abs(denominator)<.02f) return null;
        float distance=Vector3.Dot(normal,Center-position)/denominator;
        if(distance<=0 || distance>20) return null;
        var local=Vector3.Transform(position+direction*distance-Center,Quaternion.Inverse(Rotation));
        return new(local.X/Width+.5f,.5f-local.Y/Height);
    }
}

static class TvCalibration
{
    public static (float Width,float Height) Dimensions(float diagonalInches,float aspectWidth,float aspectHeight)
    {
        if(diagonalInches<=0 || aspectWidth<=0 || aspectHeight<=0) throw new ArgumentException("TV dimensions must be positive");
        float scale=diagonalInches*.0254f/MathF.Sqrt(aspectWidth*aspectWidth+aspectHeight*aspectHeight);
        return (scale*aspectWidth,scale*aspectHeight);
    }
    // Eight unknowns: TV center XYZ, TV rotation vector XYZ, local pointing yaw/pitch.
    // Ray origin is the service light-bar center; a separate grip offset is not observable here.
    static TvModel Model(double[] p,float width,float height)
    {
        var v=new Vector3((float)p[3],(float)p[4],(float)p[5]); float angle=v.Length();
        var q=angle<1e-8f?Quaternion.Identity:Quaternion.CreateFromAxisAngle(v/angle,angle);
        var a=new Vector3((float)(Math.Sin(p[6])*Math.Cos(p[7])),(float)Math.Sin(p[7]),(float)(-Math.Cos(p[6])*Math.Cos(p[7])));
        return new(width,height,new((float)p[0],(float)p[1],(float)p[2]),q,a);
    }
    static double[] Residual(double[] p,float w,float h,IReadOnlyList<AimMeasurement> samples)
    {
        var model=Model(p,w,h); var result=new double[samples.Count*3];
        for(int i=0;i<samples.Count;i++) {
            var s=samples[i]; var to=model.TargetPoint(s.Target)-s.Position;
            var delta=Vector3.Normalize(to)-Vector3.Transform(model.LocalAim,s.Orientation);
            // Huber-style weighting limits one poor aim's influence without deleting the sample.
            float length=delta.Length(); float weight=length>.035f?MathF.Sqrt(.035f/length):1;
            result[i*3]=delta.X*weight; result[i*3+1]=delta.Y*weight; result[i*3+2]=delta.Z*weight;
        }
        return result;
    }
    static double[]? Solve(double[,] a,double[] b,double tolerance=1e-12)
    {
        int n=b.Length; var matrix=(double[,])a.Clone(); var rhs=(double[])b.Clone();
        for(int k=0;k<n;k++) {
            int pivot=k; for(int i=k+1;i<n;i++) if(Math.Abs(matrix[i,k])>Math.Abs(matrix[pivot,k])) pivot=i;
            if(Math.Abs(matrix[pivot,k])<tolerance) return null;
            for(int j=k;j<n;j++) (matrix[k,j],matrix[pivot,j])=(matrix[pivot,j],matrix[k,j]);
            (rhs[k],rhs[pivot])=(rhs[pivot],rhs[k]);
            for(int i=k+1;i<n;i++) {
                double scale=matrix[i,k]/matrix[k,k];
                for(int j=k;j<n;j++) matrix[i,j]-=scale*matrix[k,j]; rhs[i]-=scale*rhs[k];
            }
        }
        var x=new double[n]; for(int i=n-1;i>=0;i--) { double value=rhs[i]; for(int j=i+1;j<n;j++) value-=matrix[i,j]*x[j]; x[i]=value/matrix[i,i]; }
        return x;
    }
    public static TvModel Fit(float width,float height,IReadOnlyList<AimMeasurement> samples)
    {
        if(samples.Count<12 || samples.Select(x=>x.Station).Distinct().Count()<3) throw new ArgumentException("Capture four corners from three positions first");
        var centers=samples.GroupBy(s=>s.Station).Select(g=>g.Aggregate(Vector3.Zero,(p,s)=>p+s.Position)/g.Count()).ToArray();
        if(!centers.Any(a=>centers.Any(b=>Vector3.Distance(a,b)>.15f))) throw new ArgumentException("Controller positions are too similar; translate at least 15 cm between rounds");
        var p=new double[8]; p[1]=height/2; // PS Eye at TV base, initial service-camera frame.
        var initial=Model(p,width,height);
        var local=samples.Aggregate(Vector3.Zero,(sum,s)=>sum+Vector3.Transform(Vector3.Normalize(initial.TargetPoint(s.Target)-s.Position),Quaternion.Inverse(s.Orientation)));
        local=Vector3.Normalize(local); p[6]=Math.Atan2(local.X,-local.Z); p[7]=Math.Asin(Math.Clamp(local.Y,-1,1));
        double lambda=.001; var r=Residual(p,width,height,samples); double cost=r.Sum(v=>v*v);
        for(int iteration=0;iteration<140;iteration++) {
            var j=new double[r.Length,8]; const double step=.0003;
            for(int k=0;k<8;k++) { var trial=(double[])p.Clone(); trial[k]+=step; var shifted=Residual(trial,width,height,samples); for(int i=0;i<r.Length;i++) j[i,k]=(shifted[i]-r[i])/step; }
            var a=new double[8,8]; var b=new double[8];
            for(int k=0;k<8;k++) { for(int i=0;i<r.Length;i++) b[k]-=j[i,k]*r[i]; for(int l=0;l<8;l++) for(int i=0;i<r.Length;i++) a[k,l]+=j[i,k]*j[i,l]; a[k,k]+=lambda; }
            var delta=Solve(a,b); if(delta is null) throw new InvalidOperationException("Calibration is underconstrained");
            var next=p.Zip(delta,(x,d)=>x+Math.Clamp(d,-.15,.15)).ToArray(); var nr=Residual(next,width,height,samples); double nc=nr.Sum(v=>v*v);
            if(double.IsFinite(nc) && nc<cost) { p=next; r=nr; if(cost-nc<1e-12) break; cost=nc; lambda=Math.Max(1e-8,lambda*.4); }
            else lambda=Math.Min(1e6,lambda*5);
        }
        var model=Model(p,width,height);
        var derivatives=new double[r.Length,8];
        for(int k=0;k<8;k++) {var trial=(double[])p.Clone(); trial[k]+=.0003; var shifted=Residual(trial,width,height,samples); for(int i=0;i<r.Length;i++) derivatives[i,k]=(shifted[i]-r[i])/.0003;}
        var gram=new double[8,8];
        for(int k=0;k<8;k++) for(int l=0;l<8;l++) for(int i=0;i<r.Length;i++) gram[k,l]+=derivatives[i,k]*derivatives[i,l];
        var normalized=new double[8,8];
        for(int k=0;k<8;k++) for(int l=0;l<8;l++) normalized[k,l]=gram[k,l]/Math.Sqrt(Math.Max(1e-30,gram[k,k]*gram[l,l]));
        if(Solve(normalized,new double[8],1e-5) is null) throw new InvalidOperationException("Calibration geometry is poorly constrained; add position/depth and tilt variation");
        if(!float.IsFinite(model.Center.Length()) || model.Center.Length()>3 || samples.Any(s=>model.Project(s.Position,s.Orientation) is null))
            throw new InvalidOperationException("No plausible forward-ray calibration; check pose axes and repeat aims");
        return model;
    }
    public static double Error(TvModel model,AimMeasurement sample)
    {
        var uv=model.Project(sample.Position,sample.Orientation);
        if(uv is null) return double.PositiveInfinity;
        // Error as fraction of physical display diagonal, independent of aspect ratio.
        var d=uv.Value-sample.Target;
        return Math.Sqrt(d.X*d.X*model.Width*model.Width+d.Y*d.Y*model.Height*model.Height)/Math.Sqrt(model.Width*model.Width+model.Height*model.Height);
    }
}
