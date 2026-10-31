// SPDX-License-Identifier: GPL-3.0-or-later
using System.Drawing.Drawing2D;
using System.Numerics;

namespace PSEyeCapture;

/// <summary>A perspective 3D attitude inspector. The untracked position is explicitly a display anchor.</summary>
sealed class PoseViewport : Control
{
    public Quaternion Orientation { get; set; } = Quaternion.Identity;
    public bool Tracking { get; set; }
    public string SourceText { get; set; } = "Waiting for DS4Windows motion data";
    float azimuth = 0.55f, elevation = 0.38f, distance = 0.48f;
    Point? drag;
    public PoseViewport() {
        DoubleBuffered = true; BackColor = Color.FromArgb(15,20,30); ForeColor = Color.White;
        SetStyle(ControlStyles.ResizeRedraw,true);
        MouseDown += (_,e) => { if(e.Button==MouseButtons.Left) { drag=e.Location; Capture=true; } };
        MouseUp += (_,_) => { drag=null; Capture=false; };
        MouseMove += (_,e) => { if(drag is {} p) { azimuth += (e.X-p.X)*0.008f; elevation = Math.Clamp(elevation+(e.Y-p.Y)*0.008f,-0.2f,1.3f); drag=e.Location; Invalidate(); } };
        MouseWheel += (_,e) => { distance = Math.Clamp(distance-e.Delta*0.0003f,0.22f,1.5f); Invalidate(); };
    }
    public void ResetView() { azimuth=0.55f; elevation=0.38f; distance=0.48f; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        if(Width<10 || Height<10) return;
        var eye = new Vector3(MathF.Sin(azimuth)*MathF.Cos(elevation),MathF.Sin(elevation),-MathF.Cos(azimuth)*MathF.Cos(elevation))*distance;
        var view=Matrix4x4.CreateLookAt(eye,Vector3.Zero,Vector3.UnitY);
        var projection=Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI/4,Width/(float)Height,0.01f,5);
        PointF Project(Vector3 p) {
            var clip=Vector4.Transform(new Vector4(p,1),view*projection);
            return new PointF(Width*(0.5f+0.5f*clip.X/clip.W),Height*(0.54f-0.5f*clip.Y/clip.W));
        }
        void Line(Vector3 a,Vector3 b,Color color,float thickness=1,bool arrow=false) {
            using var pen=new Pen(color,thickness); if(arrow) pen.CustomEndCap=new AdjustableArrowCap(3,4);
            g.DrawLine(pen,Project(a),Project(b));
        }
        for(int i=-6;i<=6;i++) {
            float v=i*0.05f; var c=Color.FromArgb(38,49,65);
            Line(new(v,-0.075f,-0.3f),new(v,-0.075f,0.3f),c);
            Line(new(-0.3f,-0.075f,v),new(0.3f,-0.075f,v),c);
        }
        var origin=new Vector3(-0.19f,-0.07f,0);
        Line(origin,origin+Vector3.UnitX*0.07f,Color.Tomato,2,true);
        Line(origin,origin+Vector3.UnitY*0.07f,Color.LightGreen,2,true);
        Line(origin,origin+Vector3.UnitZ*0.07f,Color.CornflowerBlue,2,true);
        var corners=PoseGeometry.Corners(Orientation,Vector3.Zero);
        int[][] faces=[[0,1,3,2],[4,6,7,5],[0,4,5,1],[2,3,7,6],[0,2,6,4],[1,5,7,3]];
        Color[] colors=[Color.FromArgb(48,74,114),Color.FromArgb(36,48,70),Color.FromArgb(27,38,58),Color.FromArgb(77,105,146),Color.FromArgb(45,64,95),Color.FromArgb(61,83,118)];
        foreach(int face in Enumerable.Range(0,6).OrderBy(i=>faces[i].Average(j=>Vector3.Transform(corners[j],view).Z))) {
            var polygon=faces[face].Select(i=>Project(corners[i])).ToArray();
            using var fill=new SolidBrush(Tracking?colors[face]:Color.FromArgb(45,49,57));
            using var outline=new Pen(Tracking?Color.FromArgb(150,183,222):Color.Gray,1.2f);
            if(!Tracking) outline.DashStyle=DashStyle.Dash;
            g.FillPolygon(fill,polygon); g.DrawPolygon(outline,polygon);
        }
        Vector3 Body(Vector3 p)=>Vector3.Transform(p,Orientation);
        var bar=new[]{new Vector3(-.026f,.012f,-.0495f),new Vector3(.026f,.012f,-.0495f),new Vector3(.026f,.022f,-.0495f),new Vector3(-.026f,.022f,-.0495f)};
        using(var magenta=new SolidBrush(Color.Magenta)) g.FillPolygon(magenta,bar.Select(p=>Project(Body(p))).ToArray());
        Line(Body(new(0,0,-.05f)),Body(new(0,0,-.15f)),Color.Cyan,2.5f,true);
        using var text=new SolidBrush(Color.FromArgb(220,229,244));
        using var muted=new SolidBrush(Color.FromArgb(155,170,193));
        using var title=new Font(Font.FontFamily,13,FontStyle.Bold);
        g.DrawString("Controller pose check",title,text,18,16);
        g.DrawString(SourceText,Font,Tracking?text:muted,new RectangleF(18,48,Width-36,60));
        g.DrawString("Box: 162 × 52 × 98 mm  ·  magenta = light-bar edge  ·  cyan = forward",Font,muted,18,Height-76);
        g.DrawString("Absolute position: UNTRACKED — box is at a display anchor",Font,text,18,Height-52);
        g.DrawString("Drag to orbit  ·  wheel to zoom  ·  world axes: X red / Y green / Z blue",Font,muted,18,Height-28);
    }
}
