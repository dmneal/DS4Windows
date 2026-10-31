// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

namespace PSEyeCapture;

sealed class TvCalibrationPanel : UserControl
{
    readonly ServicePose source;
    readonly ComboBox displays=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=440};
    readonly NumericUpDown diagonal=new() {Minimum=0,Maximum=200,Value=77,DecimalPlaces=1,Width=90};
    readonly NumericUpDown aspectX=new() {Minimum=1,Maximum=50,Value=16,Width=60};
    readonly NumericUpDown aspectY=new() {Minimum=1,Maximum=50,Value=9,Width=60};
    readonly CheckBox confirmed=new() {Text="I checked translation and tilt/rotation directions in the 3D view",AutoSize=true};
    readonly Label result=new() {AutoSize=true,MaximumSize=new Size(1000,0)};
    public TvCalibrationPanel(ServicePose source)
    {
        this.source=source; Dock=DockStyle.Fill;
        var layout=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(24)};
        layout.Controls.Add(new Label {Text="TV calibration · experimental geometric fit",AutoSize=true,Font=new Font(Font.FontFamily,16,FontStyle.Bold)});
        layout.Controls.Add(new Label {Text="Keep the PS Eye fixed at the TV base. Use your normal two-handed grip.\nChoose the Windows display shown on the TV; targets will fill that display.\nFor duplicated displays, select the shared desktop and ensure targets reach the TV edges (no overscan).",AutoSize=true});
        foreach(var screen in Screen.AllScreens) displays.Items.Add($"{screen.DeviceName} · {screen.Bounds.Width}×{screen.Bounds.Height} · {(screen.Primary?"primary":"extended")}");
        displays.SelectedIndex=Array.FindIndex(Screen.AllScreens,s=>s.Primary); layout.Controls.Add(displays);
        var dimensions=new FlowLayoutPanel {AutoSize=true};
        dimensions.Controls.AddRange([new Label {Text="Visible TV diagonal (inches)",AutoSize=true},diagonal,new Label {Text="Aspect",AutoSize=true},aspectX,new Label {Text=":",AutoSize=true},aspectY]);
        layout.Controls.Add(dimensions); layout.Controls.Add(confirmed);
        layout.Controls.Add(new Label {Text="1. Four inset corners from center position.\n2. Four corners after moving at least 15 cm left.\n3. Four corners from right and a different distance.\n4. Five held-out center/interior aims from a new position.\nAim, hold still, then press Cross (×) or Space. Esc exits without publishing.\nMouse input stays disabled. The result is saved locally only after validation.",AutoSize=true});
        var begin=new Button {Text="Start full-screen TV calibration",AutoSize=true};
        begin.Click+=(_,_)=> {
            if(!confirmed.Checked) {result.Text="Validate movement directions in the 3D view first."; return;}
            if(diagonal.Value<=0) {result.Text="Enter your TV's visible diagonal size in inches."; return;}
            if(source.Sample is not {Fresh:true,OrientationValid:true,PositionValid:true,Optical:true}) {result.Text="Start native capture and make the light bar optically visible first."; return;}
            var (width,height)=TvCalibration.Dimensions((float)diagonal.Value,(float)aspectX.Value,(float)aspectY.Value);
            using var wizard=new TvTargetWindow(source,Screen.AllScreens[displays.SelectedIndex],width,height,(float)diagonal.Value);
            wizard.ShowDialog(FindForm()); result.Text=wizard.Result;
        };
        layout.Controls.Add(begin); layout.Controls.Add(result); Controls.Add(layout);
    }
}

sealed class TvTargetWindow : Form
{
    readonly ServicePose source; readonly float width,height,diagonal;
    readonly string display;
    readonly System.Windows.Forms.Timer timer=new() {Interval=25};
    readonly List<ServicePoseSample> recent=[];
    readonly List<AimMeasurement> training=[],validation=[];
    readonly List<ServicePoseSample[]> recordings=[];
    static readonly Vector2[] corners=[new(.08f,.08f),new(.92f,.08f),new(.92f,.92f),new(.08f,.92f)];
    static readonly Vector2[] checks=[new(.5f,.5f),new(.3f,.3f),new(.7f,.3f),new(.7f,.7f),new(.3f,.7f)];
    int index; bool cross,solving; int? sequence; TvModel? model;
    string notice="Move to the center position; aim at the target.";
    public string Result {get;private set;}="Calibration cancelled; nothing published.";
    Vector2 Target=>index<12?corners[index%4]:checks[Math.Min(index-12,checks.Length-1)];
    public TvTargetWindow(ServicePose source,Screen screen,float width,float height,float diagonal)
    {
        this.source=source; this.width=width; this.height=height; this.diagonal=diagonal; display=screen.DeviceName;
        FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual; Bounds=screen.Bounds;
        BackColor=Color.FromArgb(13,18,28); ForeColor=Color.White; DoubleBuffered=true; KeyPreview=true;
        KeyDown+=async (_,e)=> {if(e.KeyCode==Keys.Escape) Close(); if(e.KeyCode==Keys.Space) {e.Handled=true; await CaptureTarget();}};
        timer.Tick+=async (_,_)=> {
            var s=source.Sample;
            if(s is not null && sequence!=s.Sequence) {
                sequence=s.Sequence;
                if(s is {Fresh:true,OrientationValid:true,PositionValid:true,Optical:true}) recent.Add(s); else recent.Clear();
                recent.RemoveAll(x=>Stopwatch.GetElapsedTime(x.Received).TotalSeconds>.65);
                bool pressed=s.CrossDown && !cross; cross=s.CrossDown;
                if(pressed) await CaptureTarget();
            }
            Invalidate();
        };
        Shown+=(_,_)=> {Activate(); timer.Start();}; FormClosed+=(_,_)=>timer.Dispose();
    }
    string? ReadPose(out AimMeasurement measurement,out ServicePoseSample[] used)
    {
        measurement=new(Vector3.Zero,Quaternion.Identity,Target,index/4); used=recent.ToArray();
        if(source.Sample is not {Fresh:true,OrientationValid:true,PositionValid:true,Optical:true}) return "Optical tracking lost or pose stale — expose the bar and retry.";
        if(used.Length<10 || Stopwatch.GetElapsedTime(used[0].Received,used[^1].Received).TotalSeconds<.4) return "Hold steady for at least half a second before capture.";
        var position=used.Aggregate(Vector3.Zero,(sum,s)=>sum+s.Position)/used.Length;
        var reference=used[0].Orientation; var sum=Vector4.Zero;
        foreach(var s in used) {var q=s.Orientation; float sign=Quaternion.Dot(reference,q)<0?-1:1; sum+=new Vector4(q.X,q.Y,q.Z,q.W)*sign;}
        var orientation=Quaternion.Normalize(new(sum.X,sum.Y,sum.Z,sum.W));
        if(used.Any(s=>Vector3.Distance(s.Position,position)>.015f || 2*Math.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(s.Orientation,orientation)),0,1))>Math.PI/90))
            return "Pose is moving/jittering: keep position within 15 mm and rotation within 2°.";
        measurement=new(position,orientation,Target,index<12?index/4:3); return null;
    }
    async Task CaptureTarget()
    {
        if(solving || index>=17) return;
        var error=ReadPose(out var sample,out var used);
        if(error is not null) {notice=error; return;}
        if(index is 4 or 8) {
            var previous=training.Skip(index-4).Take(4).Aggregate(Vector3.Zero,(sum,s)=>sum+s.Position)/4;
            if(Vector3.Distance(previous,sample.Position)<.15f) {notice="Translate the controller at least 15 cm for the new round, then aim again."; return;}
        }
        if(index==12 && training.GroupBy(s=>s.Station).Any(g=>Vector3.Distance(g.Aggregate(Vector3.Zero,(sum,s)=>sum+s.Position)/g.Count(),sample.Position)<.15f)) {
            notice="Use a new validation position at least 15 cm from every training position."; return;
        }
        recordings.Add(used); if(index<12) training.Add(sample); else validation.Add(sample);
        index++; recent.Clear(); notice="Captured. Aim at the next target and hold steady.";
        if(index is 4 or 8) notice=index==4?"Move LEFT at least 15 cm. Press Cross/Space only after aiming at the new target.":"Move RIGHT and change distance. Aim at the new target.";
        if(index==12) {
            solving=true; notice="Fitting TV plane and controller pointing axis…"; Invalidate();
            try {
                model=await Task.Run(()=>TvCalibration.Fit(width,height,training));
                double fit=training.Average(s=>TvCalibration.Error(model,s));
                if(fit>.03) throw new InvalidOperationException($"Training error {fit:P1} exceeds 3%; check axes, TV size and steady aims.");
                notice=$"Fit error {fit:P1} of diagonal. Move to a FOURTH position for independent validation.";
            } catch(Exception ex) {if(IsDisposed) return; Result="Fit rejected: "+ex.Message+"\nSession: "+SaveSession(false,null,null,ex.Message); MessageBox.Show(this,Result,"Calibration needs another pass"); Close();}
            finally {solving=false;}
        }
        if(index==17 && model is not null) Finish();
    }
    void Finish()
    {
        var errors=validation.Select(s=>TvCalibration.Error(model!,s)).Order().ToArray();
        double median=errors[errors.Length/2],worst=errors[^1];
        bool accepted=median<=.01 && worst<=.02;
        string path=SaveSession(accepted,double.IsFinite(median)?median:null,double.IsFinite(worst)?worst:null,null);
        Result=$"{(accepted?"Validation passed":"Validation needs improvement")}: median {median:P2}, maximum {worst:P2} of physical screen diagonal.\n{path}\nNo mouse input enabled.";
        MessageBox.Show(this,Result,"TV calibration result"); Close();
    }
    string SaveSession(bool accepted,double? median,double? worst,string? failure)
    {
        string folder=Path.Combine(AppContext.BaseDirectory,"calibration"); Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,$"tv-session-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        string config=Path.GetFullPath(Path.Combine(source.Directory,"..","..","data","PSMoveService"));
        var fingerprints=Directory.Exists(config)?Directory.GetFiles(config,"*.json").Where(f=>!f.Contains("before") && (Path.GetFileName(f).StartsWith("PS3EyeTrackerConfig_") || Path.GetFileName(f).StartsWith("dualshock4_")))
            .ToDictionary(f=>Path.GetFileName(f)!,f=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))):new Dictionary<string,string>();
        var payload=new {Version=1,Validated=accepted,CreatedUtc=DateTime.UtcNow,Display=display,PixelBounds=Bounds,
            DiagonalInches=diagonal,Model=model,MedianDiagonalError=median,MaxDiagonalError=worst,Failure=failure,DeviceConfigFingerprints=fingerprints,Training=training,Validation=validation,
            Samples=recordings,Frame="PSMoveServiceEx world, meters; quaternion XYZW; origin at light bar; ray offset zero",
            Invalidation="Moving camera/TV or changing tracker frame, controller, grip or intrinsics requires recalibration."};
        var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions {WriteIndented=true,IncludeFields=true}); File.WriteAllText(path,json);
        if(accepted) File.WriteAllText(Path.Combine(folder,"tv-calibration.json"),json);
        return path;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if(index>=17) return;
        var uv=Target; float x=uv.X*ClientSize.Width,y=uv.Y*ClientSize.Height;
        using var pen=new Pen(Color.Cyan,3); g.DrawEllipse(pen,x-18,y-18,36,36); g.DrawLine(pen,x-28,y,x+28,y); g.DrawLine(pen,x,y-28,x,y+28);
        using var text=new SolidBrush(ForeColor); using var font=new Font(Font.FontFamily,16);
        string phase=index<12?$"Training position {index/4+1}/3 · corner {index%4+1}/4":$"Independent validation · target {index-11}/5";
        string readiness=ReadPose(out _,out _)??"READY — press Cross (×) or Space";
        g.DrawString(phase+"\n"+notice+"\n"+readiness+"\nEsc cancels · hold your normal grip",font,text,new RectangleF(ClientSize.Width*.18f,ClientSize.Height*.35f,ClientSize.Width*.64f,ClientSize.Height*.35f));
        var s=source.Sample;
        if(model is not null && s is {Fresh:true,OrientationValid:true,PositionValid:true,Optical:true} && model.Project(s.Position,s.Orientation) is {} hit && hit.X>=0 && hit.X<=1 && hit.Y>=0 && hit.Y<=1) {
            using var predicted=new SolidBrush(Color.LimeGreen); g.FillEllipse(predicted,hit.X*ClientSize.Width-5,hit.Y*ClientSize.Height-5,10,10);
        }
    }
}
