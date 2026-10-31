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

Launch `PSEyeCapture.exe --tv-calibration`. The default method is now **Tape distances + 10-second reference**, with 77-inch 16:9 screen dimensions. The prior multi-position fit remains a separate experimental tab after the live attempt was rejected.

Rest the controller flat with buttons up, light bar pointing toward the TV normal, and its left/right axis parallel to the screen bottom. The screen is assumed upright. Measure straight-line lengths from all four visible screen corners to the same light-bar center without moving the controller. Choose tape units explicitly. The four distances locate the point in TV coordinates; the instructed physical attitude supplies the otherwise missing orientation constraint.

Record ten uninterrupted seconds. Hemisphere-aligned quaternion averaging estimates the reference attitude; optical position averaging anchors the corresponding point in service coordinates. Position jitter is reported, not used to restart the timer. Gaps, changed controller/stream identity, excessive angular movement/drift, insufficient optical coverage, and inconsistent tape measurements are rejected. Averaging reduces noise, not systematic camera scale/lens errors.

The resulting TV model is a rigid alignment derived from the measured point and instructed reference attitude. It is initially saved as an unvalidated survey. The subsequent five-target check holds the ray origin fixed at the surveyed location, ignoring camera depth changes. Keep the light-bar center at that location while aiming. Validation retains the existing median 1% / worst 2% screen-diagonal limits. A passing fixed-origin session is version 2, method `TapeSurveyFixedOrigin`, with an explicit fixed ray origin and reference data. It does not certify movement to another location. A future consumer must honor that mode rather than silently substituting live position.

All data remains in the user's local calibration folder. The wizard does not inject mouse input. Screen surveying, frame alignment, target checks and their persistence belong here in the DS4Windows integration; no native PSMoveServiceEx changes were needed for this workflow. Routine service bias calibration remains independent.
