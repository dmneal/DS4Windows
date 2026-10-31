// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace PSEyeCapture;

sealed class TvSurveyPanel : UserControl
{
    readonly ServicePose source;
    readonly NumericUpDown diagonal=new() {Minimum=10,Maximum=200,Value=77,DecimalPlaces=1,Width=130};
    readonly ComboBox units=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=160};
    readonly ComboBox displays=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=400};
    readonly NumericUpDown[] lengths=Enumerable.Range(0,4).Select(_=>new NumericUpDown {Minimum=0,Maximum=2000,DecimalPlaces=2,Width=160}).ToArray();
    readonly CheckBox placement=new() {AutoSize=true,Text="Controller is flat and square to the TV, buttons up, with the light bar facing straight toward it"};
    readonly Button record=new() {AutoSize=true,Text="Record 10 seconds at this position"};
    readonly Button save=new() {AutoSize=true,Text="Save recorded calibration",Enabled=false};
    readonly TextBox status=new() {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Segoe UI",12),Height=240,WordWrap=true};
    readonly System.Windows.Forms.Timer timer=new() {Interval=20};
    readonly List<ServicePoseSample> samples=[];
    long started; int? sequence; bool recording;
    double[]? distances;float width,height,recordedDiagonal;TvSurveyReference? reference;
    string? surveyPath;
    public TvSurveyPanel(ServicePose source)
    {
        this.source=source;Dock=DockStyle.Fill;Font=new Font("Segoe UI",12);AutoScaleMode=AutoScaleMode.Dpi;
        var controls=new FlowLayoutPanel {Dock=DockStyle.Fill,AutoScroll=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(20)};
        controls.Controls.Add(new Label {AutoSize=true,Text="Tape-measured TV reference",Font=new Font(Font.FontFamily,15,FontStyle.Bold)});
        controls.Controls.Add(new Label {AutoSize=true,Text="1. Leave the controller on a level surface. Buttons up; light bar points straight toward the TV.\n   Keep its left/right axis parallel to the screen's bottom edge; do not tilt it toward the screen center.\n2. Measure straight from each visible screen corner to the SAME point: the light-bar center.\n   Keep the controller fixed while measuring. Distances are diagonal tape lengths, not horizontal distances.\n3. Enter all four lengths, then record ten seconds without touching the controller.\nCamera depth jitter is averaged and reported; it does not reset the recording."});
        var size=new FlowLayoutPanel {AutoSize=true};units.Items.AddRange(["inches","cm","meters"]);units.SelectedIndex=0;
        size.Controls.AddRange([new Label {AutoSize=true,Text="TV diagonal (inches), 16:9"},diagonal,new Label {AutoSize=true,Text="Tape units"},units]);controls.Controls.Add(size);
        string[] names=["Top left","Top right","Bottom left","Bottom right"];
        for(int i=0;i<4;i++) {var row=new FlowLayoutPanel {AutoSize=true};row.Controls.AddRange([new Label {Text=names[i],AutoSize=true,MinimumSize=new Size(180,0),Margin=new Padding(3,8,12,3)},lengths[i]]);controls.Controls.Add(row);}
        foreach(var screen in Screen.AllScreens) displays.Items.Add($"{screen.DeviceName} - {screen.Bounds.Width} x {screen.Bounds.Height}");
        displays.SelectedIndex=Math.Max(0,Array.FindIndex(Screen.AllScreens,s=>s.Primary));controls.Controls.Add(displays);controls.Controls.Add(placement);
        var buttons=new FlowLayoutPanel {AutoSize=true};buttons.Controls.AddRange([record,save]);controls.Controls.Add(buttons);
        record.Click+=(_,_)=>StartRecording();
        save.Click+=(_,_)=> {try {SaveCalibration();} catch(Exception ex) {status.Text=ex.Message;}};
        void InvalidateReference() {if(recording) return;reference=null;save.Enabled=false;status.Text="Measurements changed. Record a new ten-second reference.";}
        foreach(var length in lengths) length.ValueChanged+=(_,_)=>InvalidateReference();
        diagonal.ValueChanged+=(_,_)=>InvalidateReference();units.SelectedIndexChanged+=(_,_)=>InvalidateReference();placement.CheckedChanged+=(_,_)=>InvalidateReference();
        status.Dock=DockStyle.None;controls.Controls.Add(status);Controls.Add(controls);
        void ResizeContent() {
            int available=Math.Max(360,controls.ClientSize.Width-65);
            foreach(Control child in controls.Controls) {
                if(child is Label || child is CheckBox) child.MaximumSize=new Size(available,0);
            }
            status.Width=available;
        }
        Resize+=(_,_)=>ResizeContent();ShownOnce(ResizeContent);
        timer.Tick+=(_,_)=>Tick();timer.Start();
        status.Text="Enter the four tape distances. The TV reference frame has its origin at screen center, +X right, +Y up and +Z toward you.\r\nCalibration uses tape measurements and the supported reference attitude only. No pointing targets are required. No mouse input is enabled.";
        LoadLastSurvey();
    }
    void ShownOnce(Action action) {HandleCreated+=(_,_)=>BeginInvoke(action);}
    void LoadLastSurvey()
    {
        try {
            string folder=Path.Combine(AppContext.BaseDirectory,"calibration");
            if(!Directory.Exists(folder)) return;
            string? path=Directory.GetFiles(folder,"tv-survey-*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if(path is null) return;
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var loaded=document.RootElement.GetProperty("Reference").Deserialize<TvSurveyReference>(new JsonSerializerOptions {IncludeFields=true});
            if(loaded is null) return;
            diagonal.Value=(decimal)loaded.DiagonalInches;units.SelectedIndex=0;
            for(int i=0;i<4;i++) lengths[i].Value=(decimal)Math.Round(loaded.CornerDistancesMeters[i]/.0254,2);
            placement.Checked=true;
            reference=loaded;surveyPath=path;save.Enabled=true;
            status.Text=$"Restored your four measurements and recorded reference from {File.GetLastWriteTime(path):g}.\r\nTop left / top right / bottom left / bottom right: {string.Join(" / ",lengths.Select(v=>v.Value.ToString("F2")))} inches.\r\nSave this recorded calibration, or record again if the setup or reference placement changed. No pointing check is required.";
            string savedCalibration=Path.Combine(folder,"tv-calibration.json");
            if(File.Exists(savedCalibration)) {
                using var saved=JsonDocument.Parse(File.ReadAllText(savedCalibration));
                if(saved.RootElement.TryGetProperty("SurveySession",out var session) && string.Equals(session.GetString(),path,StringComparison.OrdinalIgnoreCase))
                    status.Text="Measurement calibration is saved from this recorded reference. No pointing check is required.\r\n"+status.Text;
            }
        } catch(Exception ex) {status.Text="Could not restore the previous survey: "+ex.Message;}
    }
    void SaveCalibration()
    {
        if(reference is null || surveyPath is null) throw new InvalidOperationException("Record the ten-second reference first.");
        string folder=Path.Combine(AppContext.BaseDirectory,"calibration");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"tv-calibration.json");
        var screen=Screen.AllScreens[displays.SelectedIndex];
        string json=TvSurvey.MeasurementCalibrationJson(reference,surveyPath,screen.DeviceName);
        File.WriteAllText(path+".tmp",json);File.Move(path+".tmp",path,true);
        status.Text=$"Measurement calibration saved. No pointing check required.\r\nController position from tape (meters): {reference.Survey.PositionMeters}\r\nTape consistency error: {reference.Survey.MaxDistanceErrorMeters*100:F2} cm maximum.\r\nAveraged {reference.Tracking.Samples} samples over {reference.Tracking.Seconds:F2} seconds.\r\nOrientation RMS: {reference.Tracking.AngularRmsDegrees:F2} degrees.\r\nSaved: {path}\r\nBasis: tape distances and your confirmed flat, straight-facing placement. Screen pointing accuracy and movement to other locations have not been tested.";
    }
    void LockInputs(bool locked) {record.Enabled=!locked;placement.Enabled=units.Enabled=diagonal.Enabled=displays.Enabled=!locked;foreach(var length in lengths) length.Enabled=!locked;}
    void StartRecording()
    {
        try {
            if(!placement.Checked) throw new InvalidOperationException("Confirm the flat, straight-facing placement first.");
            if(source.Sample is not {Fresh:true,OrientationValid:true}) throw new InvalidOperationException("Start/restart the tracking service and wait for fresh orientation data.");
            double scale=units.SelectedIndex switch {0=>.0254,1=>.01,_=>1};
            distances=lengths.Select(v=>(double)v.Value*scale).ToArray();
            recordedDiagonal=(float)diagonal.Value;(width,height)=TvCalibration.Dimensions(recordedDiagonal,16,9);
            var position=TvSurvey.Locate(width,height,distances);
            samples.Clear();sequence=null;reference=null;save.Enabled=false;recording=true;started=Stopwatch.GetTimestamp();LockInputs(true);
            status.Text=$"Tape position in TV frame: {position.PositionMeters}\r\nLeave the controller untouched. Recording 0 / 10 seconds";
        } catch(Exception ex) {status.Text=ex.Message;}
    }
    void Tick()
    {
        if(!recording) return;
        var s=source.Sample;
        if(s is {Fresh:true,OrientationValid:true} && s.Sequence!=sequence) {sequence=s.Sequence;samples.Add(s);}
        double elapsed=Stopwatch.GetElapsedTime(started).TotalSeconds;
        status.Text=$"Recording {Math.Min(elapsed,10):F1} / 10.0 seconds  {samples.Count} fresh samples\r\nKeep the controller supported. Camera position jitter does not restart this timer.";
        if(elapsed<10) return;
        recording=false;LockInputs(false);
        try {
            var average=TvSurvey.Average(samples);reference=TvSurvey.Reference(recordedDiagonal,width,height,distances!,average);
            string folder=Path.Combine(AppContext.BaseDirectory,"calibration");Directory.CreateDirectory(folder);
            surveyPath=Path.Combine(folder,$"tv-survey-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(surveyPath,JsonSerializer.Serialize(new {Version=2,Validated=false,Method="TapeSurveyFixedOrigin",CreatedUtc=DateTime.UtcNow,Reference=reference,Samples=samples,
                Note="Distances establish position; flat square controller placement supplies orientation. Measurement-only calibration; no pointing check required."},new JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
            save.Enabled=true;
            SaveCalibration();
        } catch(Exception ex) {reference=null;status.Text="Reference rejected: "+ex.Message;}
    }
    protected override void Dispose(bool disposing) {if(disposing) timer.Dispose();base.Dispose(disposing);}
}
