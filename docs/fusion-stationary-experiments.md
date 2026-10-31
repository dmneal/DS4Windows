# DS4 native fusion stationary experiments — 2026-10-31

The user rested the Bluetooth DS4 on a box with the light bar visible. Four approximately 45-second recordings were taken from official PSMoveServiceEx v0.27.8. No mouse input was injected. Original controller settings were restored byte-for-byte after the temporary trials. Camera color/exposure settings were retained.

## Recorded channels

`pseye-capture.exe --pose-stream` now includes host elapsed seconds, sensor timestamp, raw gyro counts, calibrated gyro in rad/s, accelerometer in g, raw world optical quaternion, fused quaternion, independent validity and hardware-calibration status. It uses the existing native SDK stream, not another HID reader. Sensor timestamps remained -1; host receipt times and unique network sequences are used for this audit. These network samples are not guaranteed to include every underlying IMU sample.

Local raw recordings and configuration snapshots are in `artifacts/FusionAudit/` outside the repository. `utils/PSEyeCapture/analyze-fusion.py` analyzes the JSONL using Python's standard library. Example:

```powershell
python utils/PSEyeCapture/analyze-fusion.py PATH/baseline.jsonl --config PATH/original-controller-config.json
```

It excludes the first ten seconds, aligns quaternion signs for averaging, computes orientation RMS variation, endpoint rotation, world-axis rotation-vector trends, gyro mean/noise, position RMS, optical/fused disagreement and gravity consistency. Ten seconds is a defined exclusion, not proof that a restarted filter has converged. RMS orientation variation includes drift and slow motion; it is not a pure high-frequency jitter metric.

## Results

| Trial | Position RMS (mm) | Optical orientation RMS (degrees) | Fused orientation RMS (degrees) |
|---|---:|---:|---:|
| Existing settings, running service | 2.99 | 8.21 | 28.93 |
| Prediction disabled, after restart | 1.30 | 4.33 | 56.66 |
| Prediction disabled, later recording without restart | 3.39 | 8.19 | 13.56 |
| Temporary optical-derived gravity reference, prediction disabled | 3.59 | 4.04 | 41.52 |

Position and optical orientation flags were valid in all baseline frames and all frames of the later prediction-disabled recording. Startup trials had a few invalid initial frames. Validity therefore does not establish orientation accuracy. Restart transients and time-varying optical estimates prevent interpreting the table as a precise causal ranking of settings. Neither intervention demonstrated a stable usable orientation.

Across trials, stationary mean gyro was approximately (+0.07, -0.10, +0.03) degrees/s; acceleration magnitude averaged about 1.036 g. The service reported hardware calibration false. This is the service's status, not proof that the physical controller lacks factory calibration data.

## Verified upstream limitations

- `PSDualShock4Controller::getState` scales raw gyro by `gyro_gain` without subtracting per-axis bias. In `ServerRequestHandler::handle_request__set_controller_gyroscope_calibration`, the DS4 branch accepts drift/variance/gain but ignores `raw_bias`; the PS Move branch applies it. Merely running the stock DS4 gyro-noise calibration does not implement this missing bias correction.
- The upstream gyro calibration stage measures raw-count drift/noise statistics. These were calculated and retained in the reports, not written into the service as if they corrected bias. A single stationary pose also cannot calibrate gyro gain or six-face accelerometer scale/offset.
- The actual Madgwick objective is `q^-1 * d * q - s`, with identity sensor/calibration transforms for DS4. Using accepted optical quaternions and measured acceleration, the optical-implied reference direction differed from the existing identity-gravity reference by approximately 38–40 degrees. Its scatter was about six degrees in two recordings. This indicates cross-sensor inconsistency; it does not identify whether body/bar geometry, camera frame, or optical pose error is responsible.
- A temporary gravity reference estimated from the earlier recording did not stabilize the next fused run and was reverted. It is not a validated camera/body calibration.
- `eigen_quaternion_normalized_lerp` does not align quaternion hemispheres. About 5% of post-exclusion samples in one startup recording had negative fused/optical dot products; the baseline and later recording did not. This is a source-level robustness issue worth testing, not an established explanation for all observed movement.

Relevant upstream source: [DS4 driver](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/PSDualShock4/PSDualShock4Controller.cpp), [request handler](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Server/ServerRequestHandler.cpp), [orientation filter](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Filter/OrientationFilter.cpp), [quaternion interpolation](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmovemath/MathEigen.cpp), [gravity objective](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmovemath/MathAlignment.cpp).

## Next implementation boundary

Do not hide these errors with preview smoothing or certify TV calibration. Add upstream-compatible DS4 gyro bias correction and validate camera/bar/IMU frame alignment with guided multiple orientations. Replay the recordings to test sign-safe quaternion blending and gravity/optical heading separation before deploying a changed filter. Those filter changes were not made in these experiments. The 77-inch TV wizard is implemented but has no completed hardware calibration.
