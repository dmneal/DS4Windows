# Ownership across the two projects

Treat this integration as two cooperating forks with a versioned pose boundary.

| Responsibility | Owning project |
| --- | --- |
| PS Eye access, exposure/gain, lens intrinsics/distortion, color and light-bar tracking | PSMoveServiceEx |
| DS4 HID parsing, raw sensor calibration, automatic gyro bias learning, optical/IMU fusion and pose validity | PSMoveServiceEx |
| Native Config Tool device/filter controls and sensor diagnostics | PSMoveServiceEx |
| TV dimensions, selected Windows display, multi-position target wizard, controller aiming axis and ray/screen intersection | DS4Windows pointing integration |
| Profile activation buttons, mouse/keyboard mappings, cursor smoothing and Windows input injection | DS4Windows |
| Combined setup/status interface | DS4Windows may display service diagnostics and request service-owned settings; it must not apply a second hidden sensor-fusion or bias filter |

The current preview and TV fit live in the DS4Windows checkout. Sensor changes are compiled into the local PSMoveServiceEx checkout and exported as the pinned `../PSMoveServiceExFixes` bundle. The DS4Windows GitHub fork exists; a separate PSMoveServiceEx GitHub fork has not yet been created. Until that migration is made, the exported bundle preserves the exact native changes and reproducible base. Do not mistake the bundle's storage location for long-term source ownership.

When establishing the separate native fork, commit the service changes there in reviewable groups (DS4 UI/validity/calibration, build compatibility, automatic bias learning), preserve dependency patches and licenses, then pin the integration to that native commit. The existing patch applier requires the original base HEAD; update that build contract deliberately rather than committing native HEAD and silently breaking it. Keep a single authoritative native implementation, not independently edited copies in both repositories.

The pose boundary carries meters, quaternion XYZW, raw/calibrated sensor units, receipt freshness, independent position/orientation/optical validity, and controller identity. The existing helper adapts that boundary; it should not contain TV assumptions. Calibration-status sidecar files are currently local diagnostics, not a new stable network API.

TV calibration belongs to the user/display/profile layer: it stores the screen pose in the service's world frame and the controller's aiming direction for a normal grip. Recalibrate after moving the camera/TV, changing camera intrinsics/world frame/controller/grip, or changing display geometry. Routine gyro-bias updates should not invalidate screen geometry. Current session files retain whole device-config hashes for diagnostics; a future profile loader must distinguish geometry changes from normal bias updates rather than treating every hash change as invalidation.

## Current TV workflow

Launch `PSEyeCapture.exe --tv-calibration`. The default method is now **Tape distances + 10-second reference**, with 77-inch 16:9 screen dimensions. The old target-based calibration UI is no longer offered.

Rest the controller flat with buttons up, light bar pointing toward the TV normal, and its left/right axis parallel to the screen bottom. The screen is assumed upright. Measure straight-line lengths from all four visible screen corners to the same light-bar center without moving the controller. Choose tape units explicitly. The four distances locate the point in TV coordinates; the instructed physical attitude supplies the otherwise missing orientation constraint.

Record ten uninterrupted seconds. Hemisphere-aligned quaternion averaging estimates the reference attitude; optical position averaging anchors the corresponding point in service coordinates. Position jitter is reported, not used to restart the timer. Gaps, changed controller/stream identity, excessive angular movement/drift, insufficient optical coverage, and inconsistent tape measurements are rejected. Averaging reduces noise, not systematic camera scale/lens errors.

The resulting TV model is a rigid alignment derived from the measured point and instructed reference attitude. On a successful ten-second recording, measurement calibration is saved automatically to `calibration/tv-calibration.json`. Existing survey recordings can also be restored and saved without recapturing or aiming at targets. No pointing check is required.

The saved version-3 contract uses `CalibrationComplete=true`, `MeasurementValidated=true`, and `PointingValidated=false`. `ValidationMethod` identifies tape-distance consistency and reference stability. This deliberately distinguishes a complete measurement calibration from tested cursor/pointing accuracy. The file carries the fixed service ray origin, model, original reference and source-session path. A future consumer must honor its fixed-origin scope; room-wide translation remains untested.

All data remains in the user's local calibration folder. The wizard does not inject mouse input. Screen surveying, frame alignment, measurement calibration and its persistence belong here in the DS4Windows integration; no native PSMoveServiceEx changes were needed for this workflow. Routine service bias calibration remains independent.

The calibrated 3D scene is also owned by this integration. It applies the inverse saved TV rigid transform to native service poses for display and draws the screen in meters at TV-frame Z=0. It does not recenter the service, change gyro calibration, or add another pose filter. The surveyed reference has a measured TV-relative position; subsequent translations remain estimates from the existing tracking pipeline.

An optional pointing-test screen is separate from measurement calibration. Its five reference dots and live predicted aim help the user inspect behavior, with numerical distance to the selected reference. It has no calibration publishing path and does not alter measurement-completion or pointing-validation metadata. The test uses the fixed surveyed origin for tape-based calibration and live position for older multi-position calibration.
