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

Launch `PSEyeCapture.exe --tv-calibration` to open the TV setup tab. Defaults are 77 inches and 16:9 (approximately 1.705 by 0.959 meters of visible screen). Verify the selected Windows display and 3D movement directions before beginning.

Capture four inset corner targets from each of three controller locations, translating at least 15 cm between rounds and varying distance. Then capture five held-out targets from a fourth location. Use Cross or Space to capture; Escape cancels. Raw native poses are averaged over a stable half-second interval. The wizard fits the TV plane and controller pointing axis, rejects poorly conditioned fits, and only publishes calibration after held-out validation passes. All sessions remain local; no mouse input is enabled by this wizard.

The existing synthetic geometry tests pass, but the real TV fit still requires the user's aiming measurements. A displayed fit or saved file is not evidence that real-world calibration passed.
