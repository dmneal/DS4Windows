# Draft proposal: optional PS Eye light-bar pointer

Status: local design draft; no implementation or hardware validation yet.

## Problem and initial behavior

A user with a DualShock 4 and a PS3 Eye should be able to select an optional profile mode that maps the observed light-bar position to the Windows pointer. The existing controller connection, button mappings, and hold/toggle activation should continue to own those responsibilities. Camera capture must remain disabled for existing profiles unless explicitly selected.

The initial target is one DS4, one camera, and calibrated 2D cursor positioning. PS Move connectivity, metric depth, full 6DoF pose, and optical correction of orientation drift are separate future work. A single observed centroid does not establish full controller orientation.

## Capture research

The most useful native reference is [opentrack's video-ps3eye module](https://github.com/opentrack/opentrack/tree/master/video-ps3eye), its capture subprocess and [PS3EYEDriver submodule](https://github.com/opentrack/PS3EYEDriver). The wrapper demonstrates enumeration, color capture, frame-rate selection and exposure/gain control. A small native helper with timestamped samples could keep capture stalls off controller-report threads. Its IPC and deployment need an independent design; do not assume opentrack's shared-memory layout is a stable public API.

Other references are [PSMoveServiceEx](https://github.com/Timocop/PSMoveServiceEx) for a full tracking service, [psmoveapi](https://github.com/thp/psmoveapi) for future Move transport/tracking, and [PS3EyeDirectShow](https://github.com/jkevin/PS3EyeDirectShow) for webcam-style diagnostics. The dependency choice remains open pending maintainer feedback and a local capture test. Inspect source-level licenses and retain third-party notices before importing code.

## Proposed components

- A capture source publishes timestamped color frames off the input/UI threads. It reports actual mode, frame age and errors rather than merely the requested frame rate.
- A light-bar detector publishes a monotonic timestamp, camera identity, target identity, normalized centroid, area and validity/confidence. Bounded latest-sample storage avoids accumulating latency.
- Calibration maps the useful camera region to a selected screen. Mirroring and monitor selection must be explicit.
- The profile mode samples fresh optical data on the existing mapping/output path. It suppresses output on target loss and rejects stale samples from retired controller/profile sessions.
- The existing activation state and mappings remain authoritative. Camera callbacks must never inject mouse input independently.

## Existing integration points to evaluate

- `DS4Windows/DS4Control/Mouse.cs`: `Mouse`, gyro activation and `ApplyGyroToggleState`.
- `DS4Windows/DS4Control/MouseCursor.cs`: existing relative/absolute output through `Global.outputKBMHandler`.
- `DS4Windows/DS4Control/OutputKBM/VirtualKBMBase.cs` and its implementations: use the established output abstraction.
- `DS4Windows/DS4Control/ControlServiceMouseCallbackSubscription.cs`: callback retirement/lifetime behavior to preserve when adding an asynchronous source.
- `DS4WindowsTests/GyroToggleStateTests.cs`, `MouseCallbackLifetimeTests.cs`, `MousePublicationFenceTests.cs` and `ConcurrentMouseMappingTests.cs`: existing behavior that a camera feature must not regress.

These are inspected extension points, not a final promise that changes belong in each file. Keep optical-specific computation separate from the large existing mapping classes.

## Failure and lifecycle behavior

Camera removal, target occlusion, invalid samples, helper failure, profile switch, controller disconnect and application stop must suppress further optical output. Reacquisition must avoid a cursor jump until the selected mode's reactivation/rebaseline policy is satisfied. The timeout must use a monotonic clock and include capture age, not just IPC arrival time. Multi-controller association must not silently choose the largest same-colored object.

Existing button mappings must retain correct press/release semantics. Only state owned by the optical feature may be reset; target loss must not release another controller's held buttons. Existing gyro-only profiles should incur no camera resource usage or additional hot-path allocation.

## First PR boundary and validation

Prefer a first scoped implementation of capture diagnostics and testable optical sample/calibration logic before broad UI integration, subject to maintainer agreement. A later profile integration should include serialization defaults, activation behavior, output ownership, shutdown/restart and stale-sample tests.

Hardware validation must report actual PS Eye video driver/backend, camera mode and exposure, measured frame interval/drop rate, processing cost and sample-to-output latency. Test purple-bar detection under background clutter, saturation, occlusion and reacquisition. Requesting 60 fps is not proof of achieving it.

The repository's CI uses .NET 8 and x64 Release tests:

```powershell
dotnet test .\DS4WindowsTests\DS4WindowsTests.csproj -c Release -p:Platform=x64
```

No upstream build or tests have been run for this documentation-only draft. The separate workspace companion's tests do not validate DS4Windows integration.

## Maintainer decisions needed before implementation

1. Is optional external-camera pointing within the project's intended scope?
2. Should capture be a native helper, an in-process library, or an adapter to a separately installed tracker?
3. Where should per-camera calibration and per-controller target association live relative to profiles?
4. Which supported mouse modes and activation semantics should define the first PR?
