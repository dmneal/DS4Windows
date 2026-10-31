// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace PSEyeCapture;

sealed class TvSurveyPanel : UserControl
{
    readonly ServicePose source;
    readonly NumericUpDown diagonal=new() {Minimum=10,Maximum=200,Value=77,DecimalPlaces=1,Width=80};
    readonly ComboBox units=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=90};
    readonly ComboBox displays=new() {DropDownStyle=ComboBoxStyle.DropDownList,Width=400};
    readonly NumericUpDown[] lengths=Enumerable.Range(0,4).Select(_=>new NumericUpDown {Minimum=0,Maximum=2000,DecimalPlaces=2,Width=100}).ToArray();
    readonly CheckBox placement=new() {AutoSize=true,Text="Controller is flat and square to the TV, buttons up, with the light bar facing straight toward it"};
    readonly Button record=new() {AutoSize=true,Text="Record 10 seconds at this position"};
    readonly Button check=new() {AutoSize=true,Text="Check pointing on TV",Enabled=false};
    readonly TextBox status=new() {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Consolas",10)};
    readonly System.Windows.Forms.Timer timer=new() {Interval=20};
    readonly List<ServicePoseSample> samples=[];
    long started; int? sequence; bool recording;
    double[]? distances;float width,height,recordedDiagonal;TvSurveyReference? reference;
    string? surveyPath;
    public TvSurveyPanel(ServicePose source)
    {
        this.source=source;Dock=DockStyle.Fill;
        var controls=new FlowLayoutPanel {Dock=DockStyle.Top,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(12)};
        controls.Controls.Add(new Label {AutoSize=true,Text="Tape-measured TV reference",Font=new Font(Font.FontFamily,15,FontStyle.Bold)});
        controls.Controls.Add(new Label {AutoSize=true,Text="1. Leave the controller on a level surface. Buttons up; light bar points straight toward the TV.\n   Keep its left/right axis parallel to the screen's bottom edge; do not tilt it toward the screen center.\n2. Measure straight from each visible screen corner to the SAME point: the light-bar center.\n   Keep the controller fixed while measuring. Distances are diagonal tape lengths, not horizontal distances.\n3. Enter all four lengths, then record ten seconds without touching the controller.\nCamera depth jitter is averaged and reported; it does not reset the recording."});
        var size=new FlowLayoutPanel {AutoSize=true};units.Items.AddRange(["inches","cm","meters"]);units.SelectedIndex=0;
        size.Controls.AddRange([new Label {AutoSize=true,Text="TV diagonal (inches), 16:9"},diagonal,new Label {AutoSize=true,Text="Tape units"},units]);controls.Controls.Add(size);
        string[] names=["Top left","Top right","Bottom left","Bottom right"];
        for(int i=0;i<4;i++) {var row=new FlowLayoutPanel {AutoSize=true};row.Controls.AddRange([new Label {Text=names[i],Width=110},lengths[i]]);controls.Controls.Add(row);}
        foreach(var screen in Screen.AllScreens) displays.Items.Add($"{screen.DeviceName} · {screen.Bounds.Width}×{screen.Bounds.Height}");
        displays.SelectedIndex=Math.Max(0,Array.FindIndex(Screen.AllScreens,s=>s.Primary));controls.Controls.Add(displays);controls.Controls.Add(placement);
        var buttons=new FlowLayoutPanel {AutoSize=true};buttons.Controls.AddRange([record,check]);controls.Controls.Add(buttons);
        record.Click+=(_,_)=>StartRecording();
        check.Click+=(_,_)=> {
            if(reference is null) return;
            using var wizard=new TvTargetWindow(source,Screen.AllScreens[displays.SelectedIndex],reference.WidthMeters,reference.HeightMeters,reference.DiagonalInches,reference);
            wizard.ShowDialog(FindForm());status.Text=wizard.Result+"\r\nSurvey reference: "+surveyPath;
        };
        void InvalidateReference() {if(recording) return;reference=null;check.Enabled=false;status.Text="Measurements changed. Record a new ten-second reference.";}
        foreach(var length in lengths) length.ValueChanged+=(_,_)=>InvalidateReference();
        diagonal.ValueChanged+=(_,_)=>InvalidateReference();units.SelectedIndexChanged+=(_,_)=>InvalidateReference();placement.CheckedChanged+=(_,_)=>InvalidateReference();
        Controls.Add(status);Controls.Add(controls);timer.Tick+=(_,_)=>Tick();timer.Start();
        status.Text="Enter the four tape distances. The TV reference frame has its origin at screen center, +X right, +Y up and +Z toward you.\r\nOne supported reference position is calibrated first; room-wide movement still needs later validation. No mouse input is enabled.";
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
            samples.Clear();sequence=null;reference=null;check.Enabled=false;recording=true;started=Stopwatch.GetTimestamp();LockInputs(true);
            status.Text=$"Tape position in TV frame: {position.PositionMeters}\r\nLeave the controller untouched. Recording 0 / 10 seconds…";
        } catch(Exception ex) {status.Text=ex.Message;}
    }
    void Tick()
    {
        if(!recording) return;
        var s=source.Sample;
        if(s is {Fresh:true,OrientationValid:true} && s.Sequence!=sequence) {sequence=s.Sequence;samples.Add(s);}
        double elapsed=Stopwatch.GetElapsedTime(started).TotalSeconds;
        status.Text=$"Recording {Math.Min(elapsed,10):F1} / 10.0 seconds · {samples.Count} fresh samples\r\nKeep the controller supported. Camera position jitter does not restart this timer.";
        if(elapsed<10) return;
        recording=false;LockInputs(false);
        try {
            var average=TvSurvey.Average(samples);reference=TvSurvey.Reference(recordedDiagonal,width,height,distances!,average);
            string folder=Path.Combine(AppContext.BaseDirectory,"calibration");Directory.CreateDirectory(folder);
            surveyPath=Path.Combine(folder,$"tv-survey-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            File.WriteAllText(surveyPath,JsonSerializer.Serialize(new {Version=2,Validated=false,Method="TapeSurveyFixedOrigin",CreatedUtc=DateTime.UtcNow,Reference=reference,Samples=samples,
                Note="Distances establish position; flat square controller placement supplies orientation. Requires pointing validation. Translation away from this reference is not validated."},new JsonSerializerOptions {WriteIndented=true,IncludeFields=true}));
            check.Enabled=true;
            status.Text=$"Ten-second reference recorded: {average.Samples} samples over {average.Seconds:F2}s.\r\nTape position (TV meters): {reference.Survey.PositionMeters}\r\nTape residual: maximum {reference.Survey.MaxDistanceErrorMeters*100:F2} cm\r\nOptical position std dev (m): {average.OpticalPositionStdDev}\r\nOrientation RMS: {average.AngularRmsDegrees:F2}°; first/last change: {average.EndWindowRotationDegrees:F2}°\r\nSaved: {surveyPath}\r\nNext: check pointing from this same location. The averaged optical anchor is held fixed during that check; camera depth jitter cannot move the ray origin.\r\nNothing is published as validated yet.";
        } catch(Exception ex) {reference=null;status.Text="Reference rejected: "+ex.Message;}
    }
    protected override void Dispose(bool disposing) {if(disposing) timer.Dispose();base.Dispose(disposing);}
}
