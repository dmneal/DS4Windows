# PSMoveServiceEx fused-pose audit

Verified 2026-10-31 against official v0.27.8 service/client SDK and the connected USB DS4.

## Decision

Use PSMoveServiceEx's existing optical solver and sensor fusion as the intended authoritative pose source. Do not build a separate production EKF. Consume position, quaternion, timestamps and validity; add TV alignment, pointing-ray projection and DS4Windows profiles/output above that source.

Stock v0.27.8 has a verified **wired DS4 integration gap**. Resolve this before claiming its pose API works on the current USB setup. The 3D preview's gravity-aided relative IMU attitude is diagnostic only; it is not an alternative production fusion stack and does not track absolute position.

## Existing service API

[`PSMoveClient_CAPI.h`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/PSMoveClient_CAPI.h) exposes controller enumeration/listeners/streams, `PSM_GetControllerPose`, orientation/position getters, physics, calibrated/raw IMU, raw optical projections, tracking flags, button states and analog values.

[`PSM_GetControllerPose`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/PSMoveClient_CAPI.cpp) returns success for Move/DS4 only when **both position and orientation are valid**. It copies a pose even on error, so a filled struct is not evidence of valid tracking. Also require advancing state sequences/timestamps and distinguish current optical tracking from predicted orientation.

The service includes DS4 light-bar geometry, single-camera PnP pose, optical/IMU orientation and position filters, and calibration tools. These are the components to reuse. PS Move/Bluetooth support is present in source; no PS Move is connected here, so its live fused-pose quality was not tested.

## Live wired test

`pseye-capture.exe --pose-audit` uses the real client API. It enumerates controllers and, when a public DS4 is available, requests a two-second pose/physics/IMU/raw-tracker stream, counts fresh and valid states, then stops/frees the listener. It does not inject input or change pairing. JSON goes to stdout; SDK logs remain on stderr.

Actual result:

```json
{"ControllerFound":false,"Reason":"No DS4 in the service controller list"}
```

The SDK exposed controller 1 as Navi (the service's XInput/gamepad path). The service log separately records opening the USB DS4 as device 0, and DS4Windows supplies live USB IMU data through DSU. The physical DS4 is present; it is excluded from the public service list.

Source confirms two separate restrictions:

1. [`ClientRequestManager.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveclient/ClientRequestManager.cpp) sets `bIsPublicFacingController = bIsBluetooth` for PS Move and DS4. USB DS4 entries are omitted from the public C-client list.
2. [`ControllerManager.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/Device/Manager/ControllerManager.cpp) gates optical-pose/filter updates on Bluetooth or virtual controllers. [`PSDualShock4Controller.cpp`](https://github.com/Timocop/PSMoveServiceEx/blob/v0.27.8/src/psmoveservice/PSDualShock4/PSDualShock4Controller.cpp) also contains Bluetooth-specific report/output and tracking-color handling.

Removing only the discovery filter would not establish working USB fusion. This audit received no DS4 pose stream and therefore cannot measure live service-fused drift/accuracy for USB. No Bluetooth switch was attempted.

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

The audit is implemented. The USB service bridge and fused-pose renderer input are not yet implemented.
