// SPDX-License-Identifier: GPL-3.0-or-later
using System.Drawing.Drawing2D;
using System.Numerics;

namespace PSEyeCapture;

sealed class TvPointingTestPanel : UserControl
{
    public TvPointingTestPanel(ServicePose source)
    {
        Dock=DockStyle.Fill;Font=new Font("Segoe UI",12);
        var layout=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24)};
        var displays=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=550};
        foreach(var screen in Screen.AllScreens) displays.Items.Add($"{screen.DeviceName} - {screen.Bounds.Width} x {screen.Bounds.Height}");
        displays.SelectedIndex=Math.Max(0,Array.FindIndex(Screen.AllScreens,s=>s.Primary));
        var status=new Label {AutoSize=true,MaximumSize=new Size(1000,0)};
        var begin=new Button {AutoSize=true,Text="Open full-screen pointing test",Padding=new Padding(8)};
        layout.Controls.Add(new Label {AutoSize=true,Text="Pointing test - corners and center",Font=new Font(Font.FontFamily,16,FontStyle.Bold)});
        layout.Controls.Add(new Label {AutoSize=true,MaximumSize=new Size(1000,0),Text="Uses your saved measurement calibration. This test never changes or saves calibration.\nFive reference dots mark the four corners and center; a cyan dot shows the predicted aim.\nSpace, Cross, or Right Arrow selects the next reference; Left Arrow goes back; 1-5 selects a dot.\nEscape closes the test. No Windows mouse input is injected.\nFor tape calibration, keep the light-bar center at the surveyed location while changing aim."});
        layout.Controls.Add(displays);layout.Controls.Add(begin);layout.Controls.Add(status);Controls.Add(layout);
        begin.Click+=(_,_)=> {
            var calibration=TvSceneCalibration.Load(Path.Combine(AppContext.BaseDirectory,"calibration","tv-calibration.json"),out var message);
            if(calibration is null) {status.Text=message+" Save measurement calibration first.";return;}
            using var test=new TvPointingTestWindow(source,calibration,Screen.AllScreens[displays.SelectedIndex]);
            test.ShowDialog(FindForm());status.Text="Test closed. Saved calibration is unchanged.";
        };
    }
}

sealed class TvPointingTestWindow : Form
{
    readonly ServicePose source;readonly TvSceneCalibration calibration;
    readonly System.Windows.Forms.Timer timer=new() {Interval=25};
    int active;bool cross;
    static readonly string[] names=["Top left","Top right","Bottom right","Bottom left","Center"];
    public TvPointingTestWindow(ServicePose source,TvSceneCalibration calibration,Screen screen)
    {
        this.source=source;this.calibration=calibration;
        Text="TV pointing test - saved calibration is unchanged";
        FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Bounds=screen.Bounds;
        BackColor=Color.FromArgb(12,17,26);ForeColor=Color.White;DoubleBuffered=true;KeyPreview=true;
        cross=source.Sample?.CrossDown==true;
        KeyDown+=(_,e)=> {
            if(e.KeyCode==Keys.Escape) Close();
            else if(e.KeyCode is Keys.Space or Keys.Right or Keys.Enter) active=(active+1)%5;
            else if(e.KeyCode==Keys.Left) active=(active+4)%5;
            else if(e.KeyCode>=Keys.D1 && e.KeyCode<=Keys.D5) active=(int)e.KeyCode-(int)Keys.D1;
            e.Handled=true;e.SuppressKeyPress=true;Invalidate();
        };
        timer.Tick+=(_,_)=> {
            if(source.Sample is {Fresh:true} s) {if(s.CrossDown&&!cross) active=(active+1)%5;cross=s.CrossDown;}
            Invalidate();
        };
        Shown+=(_,_)=>{Activate();timer.Start();};FormClosed+=(_,_)=>timer.Dispose();
    }
    Vector2[] Targets()
    {
        float margin=28*DeviceDpi/96f;
        float x=Math.Min(.1f,margin/Math.Max(1,ClientSize.Width)),y=Math.Min(.1f,margin/Math.Max(1,ClientSize.Height));
        return [new(x,y),new(1-x,y),new(1-x,1-y),new(x,1-y),new(.5f,.5f)];
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);var g=e.Graphics;g.SmoothingMode=SmoothingMode.AntiAlias;
        var targets=Targets();float scale=DeviceDpi/96f,radius=12*scale;
        using var font=new Font("Segoe UI",12);using var title=new Font("Segoe UI",15,FontStyle.Bold);
        for(int i=0;i<targets.Length;i++) {
            float x=targets[i].X*ClientSize.Width,y=targets[i].Y*ClientSize.Height;
            using var brush=new SolidBrush(i==active?Color.Gold:Color.FromArgb(190,203,221));
            using var pen=new Pen(i==active?Color.Gold:Color.FromArgb(110,130,155),2*scale);
            g.FillEllipse(brush,x-radius*.3f,y-radius*.3f,radius*.6f,radius*.6f);g.DrawEllipse(pen,x-radius,y-radius,radius*2,radius*2);
            var label=$"{i+1} {names[i]}";var size=g.MeasureString(label,font);
            float tx=Math.Clamp(x+radius+6*scale,8*scale,Math.Max(8*scale,ClientSize.Width-size.Width-8*scale));
            float ty=i==2||i==3?y-size.Height-radius:y+radius+4*scale;
            g.DrawString(label,font,brush,tx,ty);
        }
        var sample=source.Sample;Vector2? hit=null;
        string state="Waiting for fresh controller orientation. Start/restart tracking if needed.";
        if(sample is {Fresh:true,OrientationValid:true}) {
            if(calibration.MeasurementOnly || sample.PositionValid) {
                var origin=calibration.MeasurementOnly?calibration.ServiceReference:sample.Position;
                hit=calibration.Model.Project(origin,sample.Orientation);
                if(hit is {} uv) {
                    bool onScreen=uv.X>=0&&uv.X<=1&&uv.Y>=0&&uv.Y<=1;
                    var delta=uv-targets[active];
                    double error=Math.Sqrt(delta.X*delta.X*calibration.Model.Width*calibration.Model.Width+delta.Y*delta.Y*calibration.Model.Height*calibration.Model.Height)*100;
                    state=$"{(onScreen?"Predicted aim":"Aim outside TV")}: X {uv.X:P1}, Y {uv.Y:P1} | distance from {names[active]}: {error:F1} cm";
                    if(onScreen) {
                        float px=uv.X*ClientSize.Width,py=uv.Y*ClientSize.Height;
                        using var aim=new SolidBrush(Color.Cyan);using var pen=new Pen(Color.Cyan,2*scale);
                        g.FillEllipse(aim,px-5*scale,py-5*scale,10*scale,10*scale);
                        g.DrawLine(pen,px-14*scale,py,px+14*scale,py);g.DrawLine(pen,px,py-14*scale,px,py+14*scale);
                    }
                } else state="Pointing ray does not intersect the TV in front of the controller.";
            } else state="Controller position is unavailable; expose the light bar and retry.";
        }
        using var text=new SolidBrush(ForeColor);using var muted=new SolidBrush(Color.LightSteelBlue);
        float left=ClientSize.Width*.15f,top=ClientSize.Height*.22f,width=ClientSize.Width*.7f;
        g.DrawString("POINTING TEST - "+names[active],title,text,new RectangleF(left,top,width,title.GetHeight(g)*2));
        g.DrawString(state+"\n"+(calibration.MeasurementOnly?"Origin: tape-measured reference location; camera position jitter is ignored.":"Origin: live tracked position.")+"\nGold = selected reference | cyan = predicted aim\nSpace / Cross / arrows: select reference | 1-5: direct selection | Escape: exit\nSaved calibration is unchanged. No mouse input.",font,muted,new RectangleF(left,top+title.GetHeight(g)*2,width,font.GetHeight(g)*7));
    }
}
