# TV pointing: controller, camera and multi-position calibration

Status: implementation plan, not an implemented fusion or calibration feature.

## Product goal and confirmed setup

The user points a DualShock 4 at a TV with a normal two-handed grip. The PS Eye is fixed at the base of the TV, facing the user. The pointer should follow where the controller is aimed while the user changes position in the normal seating/standing area. Camera and controller data should produce the best supported estimate, with visible confidence and an easy recenter action when tracking is insufficient.

The desired output is a calibrated pointing ray intersecting the TV plane. Controller translation and rotation both affect that intersection. This is a different calibration problem from mapping a light-bar centroid directly to screen pixels. `OpticalCalibration` currently only maps a rectangular image region and is not the final camera/controller/screen calibration model.

PSMoveServiceEx remains the owner of camera access, exposure, gain, lens calibration and optical tracking wherever its APIs and supported transport allow. DS4Windows remains the preferred owner of the USB controller, its calibrated IMU, light-bar settings, buttons, profiles and Windows mouse output. Do not create competing controller readers/output writers without explicitly resolving ownership.

### Connection requirements

The current implementation target is **USB wired DS4**. The user's phrase "USB and wired" is interpreted as wired USB now and wireless Bluetooth support later; USB is itself the wired connection. Keep that interpretation explicit until corrected. Do not require Bluetooth pairing or unplugging USB to make the initial feature work.

Normalize transport-specific reports into the same timestamped controller sample contract: stable controller identity, connection type, calibrated gyro/acceleration with explicit units/axes, buttons, device timestamp when available and host monotonic receipt time. Fusion, screen calibration and mouse mapping consume this common contract. USB and Bluetooth may have different timing/rates; keep timing estimates separate from the physical camera/TV calibration. Reconnecting the same controller over another transport should preserve valid physical alignment while requiring a sensor/timing readiness check. Future Bluetooth support must receive its own live validation; do not label it supported merely because the interface exists.

## What is already verified

- PS Eye video driver installed with user approval; actual SDK shared-memory video works through PSMoveServiceEx v0.27.8.
- The connected DS4 is USB PID `054C:05C4`, matching the original DS4 model present in that service's HID enumerator. This identifies a model, not proof of working fused USB pose.
- Diagnostic target detection works on live video. Earlier short runs measured 30.1 fps at requested 30, 58.5 at requested 60, and 67.2 at requested 75. Those stationary runs preceded the user's latest centering adjustment.
- The diagnostic's SDK helper must use the service working directory because v0.27.8 uses a relative `shared_mem` directory.
- The current code has no combined camera/IMU estimator, screen calibration wizard or DS4Windows optical profile mode.

## Reuse from PSMoveServiceEx v0.27.8

| Existing component | Planned reuse | Evidence and limits |
|---|---|---|
| Camera distortion calibration | Validate existing intrinsics; run its checkerboard workflow if needed; preserve the result in service configuration | [`AppStage_DistortionCalibration.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveconfigtool/AppStage_DistortionCalibration.cpp) uses OpenCV corner detection/calibration and warns before replacing precomputed PS Eye values. |
| Color calibration | Set camera/color parameters for reliable light-bar contours | [`AppStage_ColorCalibration.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveconfigtool/AppStage_ColorCalibration.cpp). Keep exposure/gain/FPS here; prevent another writer from changing the tracking color during use. |
| Gyroscope and optical noise calibration | Reuse the measurement methodology and compatible persisted calibration; preserve DS4Windows' factory IMU calibration when it owns the controller | [`AppStage_GyroscopeCalibration.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveconfigtool/AppStage_GyroscopeCalibration.cpp), [`AppStage_OpticalCalibration.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveconfigtool/AppStage_OpticalCalibration.cpp). The latter measures optical noise, not TV alignment. Do not apply calibration twice or confuse raw units, radians/s and degrees/s. |
| DS4 geometry and single-camera pose | Use the known light-bar model and existing contour/pose solver rather than a centroid as the complete optical observation | [`PSDualShock4Controller.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/PSDualShock4/PSDualShock4Controller.cpp) defines triangle/quad geometry; [`ServerTrackerView.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Device/View/ServerTrackerView.cpp) uses seven image/model points with `solvePnP`. |
| Orientation and position filtering | Prefer existing service filters and tune against recordings instead of starting a new general-purpose fusion stack | [`CompoundPoseFilter.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Filter/CompoundPoseFilter.cpp), [`OrientationFilter.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Filter/OrientationFilter.cpp), [`PositionFilter.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Filter/PositionFilter.cpp). DS4 config defaults reference ComplementaryOpticalARG and ComplimentaryOpticalIMU. |
| SDK data access | Consume pose, validity, raw projections, camera intrinsics and tracker pose when available | [`PSMoveClient_CAPI.h`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/PSMoveClient_CAPI.h): `PSM_GetControllerPose`, orientation/position getters, light-bar raw projection, `PSM_GetTrackerIntrinsicMatrix`, `PSM_GetTrackerPose`. Getter availability is not proof that a given USB device produces valid pose. |

## Capability gate before building the wizard

The pinned service's [`ControllerManager::updateStateAndPredict`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Device/Manager/ControllerManager.cpp) explicitly limits optical-pose/filter updates to Bluetooth or virtual controllers. DS4 output/tracking-color routines also have Bluetooth checks, and the DS4 sensor processor contains a Bluetooth-report layout. Therefore, the present USB setup must not be treated as a ready-to-use fused-pose source merely because the service discovers a controller.

First record and inspect actual controller stream timestamps, changing IMU values, optical pose/projection, validity bits and tracking state. Confirm units, handedness and axis signs with known rotations and translations. Check that a stationary stale pose is never mistaken for a fresh estimate.

Preferred ownership is DS4Windows for USB HID/LED/buttons and PSMoveServiceEx for optical tracking/fusion. If the service cannot accept DS4Windows' timestamped IMU samples through an existing suitable path, make that an explicit service-adapter extension using its DS4 tracking shape and filter inputs. Do not invent a compatible network endpoint or describe the existing external-orientation pipe as a raw-IMU fusion API: that filter accepts an external orientation and does not establish the full required bridge. A separate PSMoveServiceEx change may be necessary. Do not switch to Bluetooth or re-pair the user's controller as an implicit workaround.

## User-facing calibration flow

### 1. Camera readiness

Select the TV display and PS Eye, with the camera at its permanent base-of-TV position. Show video, detected full bar/contour, tracking quality, saturation and delivered rate. Check visibility at the intended left/right/near/far positions and screen extremes. Keep the lens switch and resolution fixed. Validate the service's precomputed intrinsics first; use its checkerboard calibration when the selected lens/mode needs correction. A physical checkerboard with known square size is suitable; do not assume displaying a pattern on a TV facing the same direction as the camera makes it visible to the camera.

Camera intrinsics correct projection/lens distortion. They do not locate the TV in the camera's coordinate system. The service's calibration-mat tracker-poses workflow is a playspace calibration, not a substitute for TV alignment, and its mat model includes PS Move-specific dimensions. With one fixed camera, use that camera as the initial reference frame and solve the TV transform in the screen stage.

### 2. Controller readiness

Keep DS4Windows' factory calibration. Ask the user to place the DS4 still for a short gyro-bias/noise measurement, with automatic motion rejection. Check accelerometer gravity magnitude and orientation during guided rotations; use broader sensor calibration only if these checks fail. Show the inferred controller forward direction and roll in a diagnostic view. The DS4 has no magnetometer, so gravity alone cannot anchor yaw.

For the confirmed normal grip, fit a fixed pointing-axis offset (boresight) and a constrained ray-origin/marker offset instead of assuming that the bar normal equals the intended pointing direction. Keep controller-body, IMU and light-bar frames explicit; avoid estimating arbitrary redundant transforms that the observations cannot identify.

### 3. Guided TV target collection

**Ask for the TV size as a required wizard step.** The normal input is visible-screen diagonal in inches or centimeters, with an editable aspect ratio (default suggestion 16:9). Offer visible width and height as an alternative. Exclude the bezel and show the derived width/height for confirmation. Validate positive finite values and save both the entered units and canonical physical dimensions. Select the actual Windows display and account for its pixel bounds and DPI separately from physical size; screen resolution alone does not supply physical TV size.

For diagonal D and aspect ratio a:b, derive width = D*a/sqrt(a*a+b*b) and height = D*b/sqrt(a*a+b*b). A target at normalized coordinates (u,v) therefore has known screen-plane coordinates (u*width,v*height), relative to the chosen screen origin. This supplies metric scale but does not by itself determine camera/TV alignment or pointing direction.

Present **one target near each of the four TV corners**, one at a time. Start with an inset of 5% of visible width/height: (0.05,0.05), (0.95,0.05), (0.95,0.95), (0.05,0.95). Record the actual target coordinates if the inset is adjusted. Do not silently treat inset targets as the exact physical corners. Keep the complete target visible and account for TV overscan or an incorrectly selected display before recording.

At each target the user aims with the normal two-handed grip, holds still and presses a chosen controller button. Collect approximately 0.5–1 second of synchronized samples when quality is sufficient rather than a single frame; reject excess motion, missing/ambiguous bar geometry, saturation and stale data. Show accepted samples, remaining targets and a redo action. Use robust aggregation and quality weights instead of treating every noisy frame as an independent precise constraint.

Repeat the four-corner sequence from several **controller positions** across the intended usage area: center, left, right and at least one different distance; add near/far or a different height as coverage requires. Prompt the user to translate the controller between rounds, not merely swivel it in place. Small grip/arm movements while aiming are allowed because the actual controller origin is measured for each sample. Record the **controller pose**, not just a label such as "left seat". The user should not need to measure each position manually when the pose estimate is observable and valid.

Fit all accepted samples jointly to estimate the TV plane/camera-to-TV transform and controller pointing alignment. Use robust weighting/outlier rejection and check parameter conditioning. Repeated center targets alone do not supply all the needed constraints. Save the measurement uncertainty and the volume actually covered by calibration.

### 4. Independent validation

Reserve one controller position and use center/interior targets excluded from fitting. Optionally include edge midpoints. Ask the user to aim there and report median and 95th-percentile error in pixels and as a fraction of the display diagonal, plus jitter, dropout and uncertainty. This checks both interior-screen accuracy and generalization to another position. Offer "add samples" or redo for a poorly covered region. Do not declare calibration successful on the four fitted corners alone.

Initial engineering targets can be median <=1% and 95th percentile <=2% of screen diagonal across the tested volume, explicitly provisional until a baseline is measured. Also measure stationary drift, acquisition time and moving-target lag; do not promise a latency bound from the current dequeue timestamps. A useful product should state where accuracy degrades rather than guarantee exact pointing anywhere in the room.

### 5. Profile activation

After validation, enable an opt-in profile mode, initially hold L2 to point. Use existing mappings for clicks/scroll/keyboard and the existing keyboard/mouse backend. Include a recenter action and a quick alignment check. Camera callbacks publish samples only; DS4Windows' existing lifetime-guarded mapping/output path decides whether to move the cursor.

## Runtime estimator and screen projection

Maintain one explicit reference frame. Use gyro data for high-rate orientation prediction, gravity for appropriate roll/pitch correction and **valid full-shape optical pose** for absolute corrections. Use the service's position estimate and its confidence, not double-integrated accelerometer position as an unlimited substitute for optical tracking. Estimate/correct systematic stream delay from motion recordings; the SDK video timestamp currently marks polling/dequeue, not exposure. Keep a timestamped IMU history so optical corrections can be applied at the appropriate past state and propagated forward.

Given controller orientation R, estimated controller origin p, calibrated local pointing axis a and local ray-origin offset o:

```text
ray origin c = p + R o
ray direction d = R a
intersection parameter t = dot(n, screenOrigin - c) / dot(n, d)
screen hit = c + t d
```

Project the hit onto the screen's horizontal/vertical basis and convert to normalized display coordinates, then to the selected monitor's Windows coordinates. Reject nearly parallel rays, intersections behind the controller and invalid input. Handle off-screen aim deliberately rather than allowing a failed geometry solve to fling the cursor to an edge. Calibrate first, then add a small residual correction only if held-out results justify it; avoid a large polynomial that merely memorizes sampled seats.

The upstream single-camera DS4 solver deliberately invalidates optical orientation beyond roughly 30 degrees of yaw or pitch because planar pose is ambiguous. Track optical **position validity separately from orientation validity**. Optical position may still be usable while orientation needs brief IMU propagation. Weight by reprojection residual, bar extent and recent consistency; do not blend a rejected optical pose as though it were an absolute truth. Planar branch flips, poor edge visibility and long occlusion must lead to lower confidence and bounded output, not indefinite dead reckoning.

## Saved calibration and invalidation

Persist a versioned calibration artifact containing camera identity/mode/lens choice and intrinsic-settings fingerprint, TV identity/physical dimensions/display selection, camera-to-TV transform, controller identity and calibrated alignment, units/axis convention, timing estimate, covered calibration volume, fit/held-out errors and uncertainty. Keep captured images/session data local unless the user explicitly exports them.

Moving the camera relative to the TV invalidates screen alignment; changing resolution/lens mode invalidates applicable camera projection assumptions. A different controller or pointing grip requires the matching controller alignment. Changes to desktop resolution should recompute pixel mapping without discarding valid physical calibration. Merely changing the user's position **inside** the validated volume must not require recalibration.

## Implementation sequence and reviewable boundaries

1. **Capability and synchronized data recorder.** Audit real USB IMU/pose paths, retain DS4Windows ownership, record raw/service pose plus validity, optical geometry and sensor arrival times. Add replay. This is the next implementation task.
2. **Upstream integration gate.** Demonstrate valid service-native DS4 light-bar pose and existing fusion with the chosen sensor source, or implement the smallest explicit PSMoveServiceEx adapter needed for USB DS4Windows samples. Test coordinate/units conversions and delayed/dropout data before promising fused position/orientation.
3. **Calibration solver without mouse injection.** Implement pose-to-ray projection, constrained robust calibration fit, persisted versioned artifacts and held-out evaluation. Use synthetic known geometry plus recorded sessions for tests.
4. **Guided calibration UI.** Reuse/deep-link service camera/color/IMU tooling where applicable; require TV size/aspect ratio, present four inset corner targets per controller position, collect normal-grip alignment and run separate center/interior/held-out-position validation. No success state until quality checks pass.
5. **DS4Windows profile and mouse integration.** Save an opt-in mode plus calibration reference, use existing activation/remapping/output, and cover disconnect, profile switching, service restart, lost target, multiple displays and stale callback retirement.

The present lightweight preview remains a diagnostic. A successful calibration file or a centroid following a target is not proof of multi-position pointing accuracy. Keep implementation and hardware-validation results explicit in each contribution.
