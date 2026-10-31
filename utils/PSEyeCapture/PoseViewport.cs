// SPDX-License-Identifier: GPL-3.0-or-later
using System.Drawing.Drawing2D;
using System.Numerics;

namespace PSEyeCapture;

/// <summary>A perspective 3D attitude inspector. The untracked position is explicitly a display anchor.</summary>
sealed class PoseViewport : Control
{
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    public bool Tracking { get; set; }
    public Vector3 Position { get; set; }
    public bool PositionTracked { get; set; }
    public bool NativeLightBarOrigin { get; set; }
    public TvSceneCalibration? TvCalibration {get;private set;}
    public string TvCalibrationStatus {get;private set;}="No saved TV calibration";
    public bool TvFrameActive=>NativeLightBarOrigin && TvCalibration is not null;
    public void SetTvCalibration(TvSceneCalibration? calibration,string status) {
        bool changed=calibration!=TvCalibration;TvCalibration=calibration;TvCalibrationStatus=status;
        if(changed) ResetView();
    }
    Vector3? anchor;
    public string PositionText { get; set; } = "Absolute position: UNTRACKED — box is at a display anchor";
    public string SourceText { get; set; } = "Waiting for DS4Windows motion data";
    float azimuth = 0.55f, elevation = 0.38f, distance = 0.48f;
    Point? drag;
    public PoseViewport() {
        DoubleBuffered = true; BackColor = Color.FromArgb(15,20,30); ForeColor = Color.White;
        SetStyle(ControlStyles.ResizeRedraw,true);
        MouseDown += (_,e) => { if(e.Button==MouseButtons.Left) { drag=e.Location; Capture=true; } };
        MouseUp += (_,_) => { drag=null; Capture=false; };
        MouseMove += (_,e) => { if(drag is {} p) { azimuth += (e.X-p.X)*0.008f; elevation = Math.Clamp(elevation+(e.Y-p.Y)*0.008f,-0.2f,1.3f); drag=e.Location; Invalidate(); } };
        MouseWheel += (_,e) => { distance = Math.Clamp(distance*MathF.Exp(-e.Delta*.001f),0.15f,TvFrameActive?30:1.5f); Invalidate(); };
    }
    public void ResetView() {
        azimuth=0.55f;elevation=TvFrameActive?.25f:.38f;
        distance=TvFrameActive?Math.Max(2.5f,Math.Max(TvCalibration!.Model.Width,TvCalibration.ReferenceInTv.Length())*2.1f):.8f;
        anchor=TvFrameActive?null:PositionTracked?Position:null;Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        if(Width<10 || Height<10) return;
        var calibration=TvFrameActive?TvCalibration:null;
        var reference=calibration?.ReferenceInTv??Vector3.Zero;
        var target=calibration is null?Vector3.Zero:reference*.5f;
        var eye = target+new Vector3(MathF.Sin(azimuth)*MathF.Cos(elevation),MathF.Sin(elevation),(calibration is null?-1:1)*MathF.Cos(azimuth)*MathF.Cos(elevation))*distance;
        var view=Matrix4x4.CreateLookAt(eye,target,Vector3.UnitY);
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI/4,Width/(float)Height,0.01f,100);
        bool Visible(Vector3 p)=>Vector4.Transform(new Vector4(p,1),view*projection).W>.01f;
        PointF Project(Vector3 p) {
            var clip=Vector4.Transform(new Vector4(p,1),view*projection);
            float w=MathF.Max(.01f,clip.W);
            return new PointF(Math.Clamp(Width*(0.5f+0.5f*clip.X/w),-50000,50000),Math.Clamp(Height*(0.54f-0.5f*clip.Y/w),-50000,50000));
        }
        void Line(Vector3 a,Vector3 b,Color color,float thickness=1,bool arrow=false) {
            if(!Visible(a)||!Visible(b)) return;
            using var pen=new Pen(color,thickness); if(arrow) pen.CustomEndCap=new AdjustableArrowCap(3,4);
            g.DrawLine(pen,Project(a),Project(b));
        }
        float step=calibration is null?.05f:.25f;
        float extent=calibration is null?.3f:Math.Max(2,reference.Z+.5f);
        float floor=calibration is null?-.075f:Math.Min(-calibration.Model.Height/2-.1f,reference.Y-.075f);
        int grid=(int)MathF.Ceiling(extent/step);
        for(int i=-grid;i<=grid;i++) {
            float v=i*step; var c=Color.FromArgb(38,49,65);
            Line(new(v,floor,-extent),new(v,floor,extent),c);
            Line(new(-extent,floor,v),new(extent,floor,v),c);
        }
        var origin=calibration is null?new Vector3(-.19f,-.07f,0):Vector3.Zero;
        float axis=calibration is null?.07f:.3f;
        Line(origin,origin+Vector3.UnitX*axis,Color.Tomato,2,true);
        Line(origin,origin+Vector3.UnitY*axis,Color.LightGreen,2,true);
        Line(origin,origin+Vector3.UnitZ*axis,Color.CornflowerBlue,2,true);
        if(calibration is null && PositionTracked && anchor is null) anchor=Position;
        var attitude=calibration?.ToTvOrientation(Orientation)??Orientation;
        var translation=calibration is null?(PositionTracked?Position-(anchor??Position):Vector3.Zero):(PositionTracked?calibration.ToTvPosition(Position):reference);
        // Upstream DS4 pose origin is the light-bar center, not the bounding-box center.
        if(NativeLightBarOrigin) translation+=Vector3.Transform(new Vector3(0,-.017f,.0495f),attitude);
        var corners=PoseGeometry.Corners(attitude,translation);
        int[][] faces=[[0,1,3,2],[4,6,7,5],[0,4,5,1],[2,3,7,6],[0,2,6,4],[1,5,7,3]];
        Color[] colors=[Color.FromArgb(48,74,114),Color.FromArgb(36,48,70),Color.FromArgb(27,38,58),Color.FromArgb(77,105,146),Color.FromArgb(45,64,95),Color.FromArgb(61,83,118)];
        var surfaces=faces.Select((face,i)=>(Vertices:face.Select(j=>corners[j]).ToArray(),Fill:Tracking?colors[i]:Color.FromArgb(45,49,57),Outline:Tracking?Color.FromArgb(150,183,222):Color.Gray,Width:1.2f)).ToList();
        if(calibration is not null) surfaces.Add((calibration.ScreenCorners,Color.FromArgb(24,43,64),Color.LightSkyBlue,3));
        foreach(var surface in surfaces.OrderBy(f=>f.Vertices.Average(p=>Vector3.Transform(p,view).Z))) {
            if(surface.Vertices.Any(p=>!Visible(p))) continue;
            var polygon=surface.Vertices.Select(Project).ToArray();
            using var fill=new SolidBrush(surface.Fill);
            using var outline=new Pen(surface.Outline,surface.Width);
            if(!Tracking && surface.Width<2) outline.DashStyle=DashStyle.Dash;
            g.FillPolygon(fill,polygon); g.DrawPolygon(outline,polygon);
        }
        if(calibration is not null) {
            Line(origin,origin+Vector3.UnitX*axis,Color.Tomato,2,true);
            Line(origin,origin+Vector3.UnitY*axis,Color.LightGreen,2,true);
            Line(origin,origin+Vector3.UnitZ*axis,Color.CornflowerBlue,2,true);
        }
        Vector3 Body(Vector3 p)=>Vector3.Transform(p,attitude)+translation;
        var bar=new[]{new Vector3(-.026f,.012f,-.0495f),new Vector3(.026f,.012f,-.0495f),new Vector3(.026f,.022f,-.0495f),new Vector3(-.026f,.022f,-.0495f)};
        if(bar.All(p=>Visible(Body(p)))) using(var magenta=new SolidBrush(Color.Magenta)) g.FillPolygon(magenta,bar.Select(p=>Project(Body(p))).ToArray());
        Line(Body(new(0,0,-.05f)),Body(new(0,0,-.15f)),Color.Cyan,2.5f,true);
        using var text=new SolidBrush(Color.FromArgb(220,229,244));
        using var muted=new SolidBrush(Color.FromArgb(155,170,193));
        using var title=new Font(Font.FontFamily,13,FontStyle.Bold);
        g.DrawString(calibration is null?"Controller pose check":"Controller and calibrated TV",title,text,18,16);
        float sourceTop=20+title.GetHeight(g),sourceHeight=Font.GetHeight(g)*2.5f;
        g.DrawString(SourceText,Font,Tracking?text:muted,new RectangleF(18,sourceTop,Width-36,sourceHeight));
        if(calibration is not null) {
            var label=new Vector3(-calibration.Model.Width/2,calibration.Model.Height/2,0);
            if(Visible(label)) g.DrawString($"TV {calibration.Model.Width:F2} x {calibration.Model.Height:F2} m",Font,text,Project(label));
            g.DrawString(TvCalibrationStatus,Font,muted,new RectangleF(18,sourceTop+sourceHeight+4,Width-36,Font.GetHeight(g)*2));
        } else if(NativeLightBarOrigin) g.DrawString(TvCalibrationStatus,Font,muted,new RectangleF(18,sourceTop+sourceHeight+4,Width-36,Font.GetHeight(g)*2));
        g.DrawString("Box: 162 × 52 × 98 mm  ·  magenta = light-bar edge  ·  cyan = forward",Font,muted,18,Height-76);
        g.DrawString(PositionText,Font,text,18,Height-52);
        g.DrawString(calibration is null?"Drag to orbit; wheel to zoom; world axes: X red / Y green / Z blue":"TV-centered axes: +X right / +Y up / +Z toward viewer; drag to orbit, wheel to zoom",Font,muted,18,Height-28);
    }
}
