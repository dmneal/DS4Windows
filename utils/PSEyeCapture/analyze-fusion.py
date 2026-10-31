"""Offline native fusion audit; Python standard library only. No hardware writes."""
import argparse, json, math, statistics
from pathlib import Path

def mean(v): return [statistics.mean(x) for x in zip(*v)]
def norm(v): return math.sqrt(sum(x*x for x in v))
def unit(v):
    n=norm(v)
    return [x/n for x in v] if n else v
def multiply(a,b):
    x,y,z,w=a; X,Y,Z,W=b
    return [w*X+x*W+y*Z-z*Y,w*Y-x*Z+y*W+z*X,w*Z+x*Y-y*X+z*W,w*W-x*X-y*Y-z*Z]
def relative(a,b): return multiply(b,[-a[0],-a[1],-a[2],a[3]])
def rotate(q,v): return multiply(multiply(q,v+[0]),[-q[0],-q[1],-q[2],q[3]])[:3]
def vector_angle(a,b): return math.degrees(math.acos(max(-1,min(1,sum(x*y for x,y in zip(unit(a),unit(b)))))))
def angle(a,b): return 2*math.degrees(math.acos(min(1,abs(sum(x*y for x,y in zip(unit(a),unit(b)))))))
def average_quat(q):
    base=q[0]
    return unit(mean([[v*(-1 if sum(x*y for x,y in zip(base,a))<0 else 1) for v in a] for a in q]))
def rotation_vector(q):
    q=unit(q)
    if q[3]<0: q=[-x for x in q]
    s=norm(q[:3]); a=2*math.atan2(s,q[3])*180/math.pi
    return [x*a/s for x in q[:3]] if s>1e-10 else [0,0,0]
def regress(t,y):
    tm=statistics.mean(t); ym=statistics.mean(y)
    slope=sum((x-tm)*(v-ym) for x,v in zip(t,y))/sum((x-tm)**2 for x in t)
    return {"SlopeDegreesPerSecond":slope,"DetrendedRmsDegrees":math.sqrt(statistics.mean((v-ym-slope*(x-tm))**2 for x,v in zip(t,y)))}
def analyze(path,reference_gravity=None):
    rows=[json.loads(line) for line in Path(path).read_text().splitlines() if line.strip()]
    settled=[r for r in rows if r['HostSeconds']>=10]
    if len(settled)<50: raise ValueError('Need at least ten seconds settling and 50 later frames')
    t=[r['HostSeconds'] for r in settled]; p=mean([r['Position'] for r in settled])
    result={"File":str(path),"Frames":len(rows),"SettledFrames":len(settled),"DurationSeconds":rows[-1]['HostSeconds'],
        "SettlingExcludedSeconds":10,"ValidPoseFraction":statistics.mean(r['PositionValid'] and r['OrientationValid'] for r in rows),
        "OpticalOrientationFraction":statistics.mean(r['OpticalOrientationValid'] for r in rows),
        "HardwareCalibration":rows[-1]['HardwareCalibration'],"DistinctSensorTimestamps":len(set(r['SensorSeconds'] for r in rows)),
        "GyroMeanDegreesPerSecond":[x*180/math.pi for x in mean([r['GyroRad'] for r in settled])],
        "GyroStdDegreesPerSecond":[statistics.pstdev(x)*180/math.pi for x in zip(*[r['GyroRad'] for r in settled])],
        "AccelMeanG":mean([r['AccelG'] for r in settled]),"AccelNormMeanG":statistics.mean(norm(r['AccelG']) for r in settled),
        "PositionRmsMm":1000*math.sqrt(statistics.mean(sum((v-c)**2 for v,c in zip(r['Position'],p)) for r in settled))}
    for field in ['Quaternion','OpticalQuaternion']:
        valid=[r for r in settled if field=='Quaternion' or r['OpticalOrientationValid']]
        q=[unit(r[field]) for r in valid]; tt=[r['HostSeconds'] for r in valid]
        reference=average_quat(q[:min(60,len(q))]); vectors=[rotation_vector(relative(reference,a)) for a in q]
        qmean=average_quat(q)
        result[field]={"EndpointChangeDegrees":angle(reference,average_quat(q[-60:])),
            "AxesWorldXYZ":[regress(tt,list(v)) for v in zip(*vectors)],
            "RmsFromMeanDegrees":math.sqrt(statistics.mean(angle(qmean,a)**2 for a in q))}
    result['FusedOpticalDisagreementMeanDegrees']=statistics.mean(angle(r['Quaternion'],r['OpticalQuaternion']) for r in settled if r['OpticalOrientationValid'])
    result['NegativeFusedOpticalQuaternionDotFraction']=statistics.mean(sum(a*b for a,b in zip(r['Quaternion'],r['OpticalQuaternion']))<0 for r in settled if r['OpticalOrientationValid'])
    result['OpticalQuaternionSignFlips']=sum(sum(a*b for a,b in zip(a['OpticalQuaternion'],b['OpticalQuaternion']))<0 for a,b in zip(settled,settled[1:]) if a['OpticalOrientationValid'] and b['OpticalOrientationValid'])
    raw=[r['RawGyro'] for r in settled]
    result['GyroCalibrationMeasurement']={"RawBias":mean(raw),"MaxAbsRawMean":max(abs(x) for x in mean(raw)),
        "MaxUnsignedRawSampleVariance":max(statistics.variance(abs(v) for v in axis) for axis in zip(*raw)),
        "Note":"Matches upstream noise-statistic units; stock DS4 calibration handler ignores raw_bias, so these are not a bias correction."}
    gravity=[unit(rotate(unit(r['OpticalQuaternion']),unit(r['AccelG']))) for r in settled if r['OpticalOrientationValid']]
    gmean=unit(mean(gravity))
    result['GravityConsistency']={"OpticalImpliedWorldGravity":gmean,
        "ScatterRmsDegrees":math.sqrt(statistics.mean(vector_angle(gmean,g)**2 for g in gravity)),
        "ReferenceGravity":reference_gravity,
        "AngleToReferenceGravityDegrees":vector_angle(gmean,reference_gravity) if reference_gravity is not None else None,
        "Note":"Uses upstream objective q^-1*d*q=s. Single stationary pose cannot establish full accelerometer scale or body/bar alignment."}
    return result

if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('recordings',nargs='+'); parser.add_argument('--config',help='Controller config snapshot used for the recordings')
    args=parser.parse_args(); gravity=None
    if args.config:
        g=json.loads(Path(args.config).read_text(encoding='utf-8-sig'))['Calibration']['Identity']['Gravity']
        gravity=[float(g[k]) for k in ['X','Y','Z']]
    for filename in args.recordings:
        report=analyze(filename,gravity); out=Path(filename).with_suffix('.summary.json'); out.write_text(json.dumps(report,indent=2)); print(json.dumps(report,indent=2))
