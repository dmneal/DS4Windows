# Draft proposal: optional PS Eye light-bar pointer

Status: initial diagnostic implementation is in `utils/PSEyeCapture`; hardware validation and profile integration are pending. Per the user's selected architecture, **PSMoveServiceEx owns PS Eye access and camera configuration**.

The product target has since been refined: camera at the TV base, normal two-handed DS4 grip, and calibrated camera/IMU pointing from multiple user positions. The current implementation plan is [TV pointing calibration](tv-pointing-calibration-plan.md). The image-coordinate pointer described below is an intermediate diagnostic, not the final multi-position pointing model.

Current scope is USB wired first, with a common sensor contract for later Bluetooth validation. The planned wizard asks for TV size, presents four inset corner targets at each of several controller positions, and checks center/interior targets from a held-out position before enabling pointing.

## Problem and initial behavior

A user with a DualShock 4 and a PS3 Eye should be able to select an optional profile mode that maps the observed light-bar position to the Windows pointer. The existing controller connection, button mappings, and hold/toggle activation should continue to own those responsibilities. Camera capture must remain disabled for existing profiles unless explicitly selected.

The initial target is one DS4, one camera, and calibrated 2D cursor positioning. PS Move connectivity, metric depth, full 6DoF pose, and optical correction of orientation drift are separate future work. A single observed centroid does not establish full controller orientation.

## Capture research

The selected backend is [PSMoveServiceEx](https://github.com/Timocop/PSMoveServiceEx), using its official v0.27.8 C client SDK. The diagnostic adapter discovers trackers, starts a tracker stream and reads SDK shared-memory BGR frames. Exposure, gain, FPS, camera drivers and camera calibration remain in PSMoveServiceEx Config Tool / Virtual Device Manager. DS4Windows retains its existing controller transport and input mapping. The adapter does not require PSMoveServiceEx to take ownership of the DS4 just to receive camera images.

opentrack's capture code remains a research reference; a separate direct-driver dependency is not included. psmoveapi remains a reference for later Move support. Preserve SDK source/license notices when packaging.

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

Local validation: DS4Windows x64 Release suite passed 7,205 tests with 26 opt-in tests skipped; 29 isolated optical checks and an eight-frame synthetic adapter-pipe test passed. After approved installation of the service's PS Eye video driver, the SDK discovered one PS Eye and delivered 120 live VGA frames. At requested 75 fps the diagnostic measured 67.2 delivered fps and detected a purple target in 106/120 frames. At requested 60 fps it measured 58.5 fps with 120/120 detections. The SDK helper must share the service working directory for its relative `shared_mem` path. Moving-target behavior, calibration and cursor integration remain unvalidated. The separate workspace companion's tests do not validate DS4Windows integration.

## Maintainer decisions needed before implementation

1. Is optional external-camera pointing within the project's intended scope?
2. Is a helper consuming the separately installed PSMoveServiceEx SDK acceptable for the opt-in capture source?
3. Where should per-camera calibration and per-controller target association live relative to profiles?
4. Which supported mouse modes and activation semantics should define the first PR?
