# PSMoveServiceEx fused-pose audit

Verified 2026-10-31 against official v0.27.8 service/client SDK, first over USB and then with the user's paired Bluetooth DS4.

## Bluetooth result: native fused pose works

The live Bluetooth retest delivered **646 fresh states, 641 valid poses, and 641 optically tracked states in ten seconds**. Orientation was valid in all 646 states; position was valid in 641. The final `PSM_GetControllerPose` result was success, tracking was enabled, and the raw tracker mask was 1. DS4Windows remained running. This validates access to the built-in pose pipeline, not movement accuracy, long-term drift, or exclusive LED ownership.

After the camera was unplugged/replugged, the service still advertised a tracker but supplied a rendered `Tracker timed out` frame. Restarting the workspace service restored real video. The new USB path (`b1_p2.1`) received a new default camera configuration rather than the previous path's settings.

The default magenta threshold rejected the actual bar: measured OpenCV-style hue averaged 144/180, saturation 126/255, and value 191/255 in the visible bar, while the default saturation minimum was 223. A backed-up local configuration was adjusted to 60 Hz, exposure 8, gain 32, and magenta H=144±16, S=160±80, V=170±85. The broad diagnostic detector then saw the bar in 120/120 frames at 59.2 fps; the subsequent native pose audit succeeded. These are scene-specific color settings, not camera intrinsic calibration or universal defaults.

`SensorClockAdvanced` remained false even while network state sequences and valid optical poses advanced. Do not use that field alone as a freshness gate; investigate SDK sensor timestamp population before time synchronization work. The GUI now consumes native position/quaternion by default through the C API adapter. A live end-to-end probe recorded 180/180 valid, optically tracked poses and rendered the final pose. DSU attitude is an explicit diagnostic alternative.

## Components to reuse

- `ServerTrackerView.cpp`: fits the DS4 light-bar contour to known triangle/quad geometry, solves camera pose with OpenCV `solvePnP`, and rejects ambiguous optical orientation beyond about 30 degrees yaw/pitch.
- `OrientationFilter.cpp`, `OrientationFilterComplementaryOpticalARG::update`: blends synchronized optical quaternion observations using projection/variance quality, then applies the Madgwick ARG IMU update. This is an existing complementary pipeline, not a requirement to create an EKF.
- `PositionFilter.cpp`, `PositionFilterComplimentaryOpticalIMU::update`: initializes from a valid optical position, smooths subsequent optical updates, and runs IMU updates after initialization. The current controller configuration disables linear acceleration for translation.

Consume this service pose directly for Bluetooth, retaining independent validity, optical visibility and freshness. Preserve the wired work item below: Bluetooth success does not repair the USB transport restrictions. If a service extension is needed for wired DS4Windows IMU, feed the existing filter packet path rather than duplicating these filters in the UI.

## Decision

Use PSMoveServiceEx's existing optical solver and sensor fusion as the intended authoritative pose source. Do not build a separate production EKF. Consume position, quaternion, timestamps and validity; add TV alignment, pointing-ray projection and DS4Windows profiles/output above that source.

Stock v0.27.8 has a verified **wired DS4 integration gap**. Resolve this before claiming its pose API works on the current USB setup. The 3D preview's gravity-aided relative IMU attitude is diagnostic only; it is not an alternative production fusion stack and does not track absolute position.

## Existing service API

[`PSMoveClient_CAPI.h`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/PSMoveClient_CAPI.h) exposes controller enumeration/listeners/streams, `PSM_GetControllerPose`, orientation/position getters, physics, calibrated/raw IMU, raw optical projections, tracking flags, button states and analog values.

[`PSM_GetControllerPose`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/PSMoveClient_CAPI.cpp) returns success for Move/DS4 only when **both position and orientation are valid**. It copies a pose even on error, so a filled struct is not evidence of valid tracking. Also require advancing state sequences/timestamps and distinguish current optical tracking from predicted orientation.

The service includes DS4 light-bar geometry, single-camera PnP pose, optical/IMU orientation and position filters, and calibration tools. These are the components to reuse. PS Move/Bluetooth support is present in source; no PS Move is connected here, so its live fused-pose quality was not tested.

## Live wired test

`pseye-capture.exe --pose-audit` uses the real client API. It enumerates controllers and, when a public DS4 is available, requests a ten-second pose/physics/IMU/raw-tracker stream, counts fresh and valid states, then stops/frees the listener. It does not inject input or change pairing. JSON goes to stdout; SDK logs remain on stderr.

Actual result:

```json
{"ControllerFound":false,"Reason":"No DS4 in the service controller list"}
```

The SDK exposed controller 1 as Navi (the service's XInput/gamepad path). The service log separately records opening the USB DS4 as device 0, and DS4Windows supplies live USB IMU data through DSU. The physical DS4 is present; it is excluded from the public service list.

Source confirms two separate restrictions:

1. [`ClientRequestManager.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/ClientRequestManager.cpp) sets `bIsPublicFacingController = bIsBluetooth` for PS Move and DS4. USB DS4 entries are omitted from the public C-client list.
2. [`ControllerManager.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Device/Manager/ControllerManager.cpp) gates optical-pose/filter updates on Bluetooth or virtual controllers. [`PSDualShock4Controller.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/PSDualShock4/PSDualShock4Controller.cpp) also contains Bluetooth-specific report/output and tracking-color handling.

Removing only the discovery filter would not establish working USB fusion. This audit received no DS4 pose stream and therefore cannot measure live service-fused drift/accuracy for USB. This was the earlier USB-only test; the later Bluetooth result is documented above.

## Implementation choice

| Approach | Assessment |
|---|---|
| Consume valid native service pose | Preferred. Reuse its geometry/calibration/filters; frontend handles TV alignment, visualization and existing DS4Windows mappings/output. |
| Feed USB/DS4Windows IMU into existing service tracking/filter inputs | Preferred wired integration work if native USB transport is insufficient. Preserve one HID/button/LED owner and explicit time/unit/frame conversions. A service-side source extension may be needed. |
| Build an independent application EKF | Deferred. Only reconsider with recordings showing a limitation in the existing algorithms. |
| Treat the relative DSU 3D preview as fused pose | Incorrect. It has gravity-aided tilt and relative yaw; its absolute translation is untracked. |

Built-in fusion does not guarantee drift-free tracking: calibration, optical visibility, planar ambiguity, stream delay and valid absolute corrections still matter. The stock single-camera DS4 solver rejects optical orientation outside approximately 30 degrees yaw/pitch. Keep position/orientation/optical validity separate, and stop or degrade predictably when estimates become stale.

## Next boundary

Define a pose-sample contract with identity/transport, source timing and sequence, meters, a documented quaternion frame, independent validity and confidence. Resolve wired access into the **existing** service pipeline, then feed its accepted pose directly to the 3D view without filtering it again. Build multi-position TV calibration and mouse mapping on that contract. The external-orientation pipe is not automatically a raw-IMU fusion bridge.

The audit and native Bluetooth fused-pose renderer are implemented. The USB service bridge, physical axis validation and TV pointing calibration remain pending.
