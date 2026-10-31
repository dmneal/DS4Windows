# PSMoveServiceEx PS Eye diagnostic

Experimental first stage of optional camera-based pointing for DS4Windows. **PSMoveServiceEx owns camera discovery, USB access, exposure/gain/FPS and calibration.** This tool uses its actual C client SDK to receive local shared-memory video. It does not use a separate PS Eye driver implementation, pretend a custom JSON protocol is PSMoveServiceEx, or require SteamVR.

The diagnostic previews video, detects the purple DS4 bar and offers a 3D box for controller IMU/axis checks. It does not inject mouse input or add a profile mode yet. The small tracking classes are compiled by the main application but no camera worker is created in DS4Windows. Existing controller transport and mappings remain with DS4Windows. Connecting the DS4 itself to PSMoveServiceEx is not required for camera-only acquisition.

## First calibration step: controller in 3D

The **Controller in 3D** tab shows an orbitable 162×52×98 mm box ([Sony's approximate dimensions](https://sonyinteractive.com/en/press-releases/2013/playstation4-ps4-design-and-price-unveiled-available-at-399-in-us-and-at-399-in-europe/)), a magenta light-bar edge and cyan forward arrow. Drag to orbit; use the wheel to zoom. The Camera tracking tab retains the live video.

Enable DS4Windows' loopback UDP server at `127.0.0.1:26760`, with the controller in slot 1. The view reads calibrated gyro/accelerometer samples without opening HID, integrates device-time angular velocity and corrects tilt toward gravity. Set forward establishes a local reference. Measure gyro bias requests a stationary one-second measurement. This is **diagnostic attitude, not production optical/IMU fusion**: yaw is relative and absolute translation is untracked. The box remains at a labelled display anchor. Stale data turns it grey/dashed; gaps/device changes reset the reference.

USB telemetry was verified with 100 live samples. Physical axis directions still need guided user checks. Bluetooth has not been tested. Intended production orientation/position will come from valid PSMoveServiceEx fused pose after resolving the [wired integration gap](../../docs/psmoveserviceex-fusion-audit.md).

`PSEyeCapture.exe --pose-probe` collects motion and writes `pose-probe.json` plus a rendered `pose-probe.png`. It never asserts tracked translation. DS4Windows 3.0.18 leaves the activity byte zero even in valid connected-controller packets; connection metadata, CRC and sample freshness are validated instead.

## Setup and run

1. Install/configure [PSMoveServiceEx](https://github.com/Timocop/PSMoveServiceEx/releases/tag/v0.27.8). Its [PS Eye guide](https://github.com/Timocop/PSMoveServiceEx/wiki/Setting-up-the-PlayStation-3-Eye-Camera) uses **Virtual Device Manager → Service Management → Install PlayStation Eye Drivers** for camera drivers. The video interface is `USB\VID_1415&PID_2000&MI_00`; retain the camera's audio interface. This tool installs no drivers.
2. Run `PSMoveService.exe`. Verify the PS Eye in `PSMoveConfigTool.exe`. Select 640×480 and request at least 60 fps. Configure exposure/gain and calibrate the color there. Stop this diagnostic before changing the camera's video mode.
3. Run `PSEyeCapture.exe` from its package folder. Select the service tracker-list index and **Service folder** (the service's working directory containing `shared_mem`), then **Start / Apply**. When one accessible running service is found, its executable folder is suggested automatically. Stop closes the helper. A three-second stall terminates it; retry explicitly after fixing the service/device.

PSMoveServiceEx v0.27.8 compiles its Boost shared-memory directory as the relative path `shared_mem`. The SDK helper **must run with the same working directory as the service**; a network connection alone is insufficient. Use the Service folder field, or pass `--service-dir "C:\path\to\PSMoveService"`. SDK DLL loading remains restricted to the helper's own directory.

Pass `--start` to open the live preview with capture already running. Without it, press Start / Apply in the window.

The preview displays delivered FPS, age since SDK-frame dequeue, normalized target coordinates and a green target circle. FPS is measured at the pipe consumer; timestamps are Windows QPC **after SDK video polling**, not camera exposure timestamps. Neither measures optical-to-mouse latency. The preview renders at approximately 30 Hz and allocates bitmaps for diagnostics.

The helper follows the SDK lifecycle: `PSM_Initialize`, `PSM_GetTrackerList`, `PSM_AllocateTrackerListener`, `PSM_StartTrackerDataStream`, `PSM_OpenTrackerVideoStream`, then `PSM_Update` / `PSM_PollTrackerVideoStream` / `PSM_GetTrackerVideoFrameBuffer`. Normal termination closes the stream, stops data, frees the listener and shuts down the client. Process termination relies on the service retiring the disconnected client. Startup/reads are off the UI and controller-report threads. SDK console logging is routed to stderr, keeping the binary frame channel separate.

`pseye-capture.exe --list` queries the real service and reports trackers to stderr (exit 4 for none). The service endpoint is fixed to loopback port 9512 because SDK video uses local shared memory. No camera or DS4 devices are opened directly by this adapter.

`pseye-capture.exe --pose-audit` queries service controllers and, when a public DS4 exists, measures pose/validity/IMU stream state for ten seconds. JSON goes to stdout; diagnostics go to stderr. On USB the SDK excludes the DS4 from its public list. On Bluetooth, after scene color calibration, the live audit obtained 641 valid optically tracked poses from 646 fresh states. See the fusion audit for settings and limitations. Exit 0 means the audit completed, not that valid fused pose was available. Run from the service working directory.

`PSEyeCapture.exe --synthetic` previews a simulated purple rectangle after Start. `PSEyeCapture.exe --self-test` consumes eight synthetic native frames headlessly, writes `self-test.txt` and exits with 0 on success. These modes do not connect to the service or access a camera, and do not prove that hardware capture works.

`PSEyeCapture.exe --probe --service-dir "C:\path\to\PSMoveService"` captures 120 real frames without injecting input. It writes `camera-probe.json` (frame count, delivered rate, target count), `camera-probe.log` and `camera-probe.bmp` locally beside the executable. The image can contain the camera's surroundings; it is not uploaded by this tool.

## Build on Windows x64

Requirements: .NET 8 SDK for Windows, official [Zig 0.14.1](https://ziglang.org/download/), and the extracted **PSMoveClient_CAPI.zip** from [PSMoveServiceEx v0.27.8](https://github.com/Timocop/PSMoveServiceEx/releases/tag/v0.27.8). `PsmSdkRoot` points at the extracted directory containing `include`, `bin`, and `LICENSE.txt`.

```powershell
.\utils\PSEyeCapture\build.ps1 -ZigExe C:\tools\zig\zig.exe -PsmSdkRoot C:\tools\PSMoveClient_CAPI
```

The script runs 46 synthetic tracking/attitude/geometry checks, builds the small SDK adapter, publishes the preview, copies the SDK DLL/license and runs the eight-frame native-pipe test. Output is `utils/PSEyeCapture/bin/package/`. No compiler, SDK or driver is downloaded automatically. The .NET projects have no NuGet dependencies and use an offline NuGet config. The SDK DLL is loaded only from beside the native helper. Tested SDK ZIP SHA-256: `95996e356b25eafbdf7e43c13852af299eb392cb169d71310110a43d62a006dc`.

## Tracking and limits

Color detection operates on PSMoveServiceEx's BGR video. Thresholds: HSV hue 250–350 degrees, saturation at least 0.30, brightness at least 90/255. Eight-connected components smaller than 15 pixels or larger than 20% of the image are rejected. Acquisition requires a single eligible component; subsequent frames use nearest-component association within normalized distance 0.20. This can still misidentify nearby purple objects and is not a physical-controller identity guarantee.

`OpticalCalibration` provides a rectangular camera region, mirror, clamp and 150-ms freshness gate; the preview currently uses the whole frame. Invalid, future and stale samples do not project. Buffers are reused in detection. No absolute 3D tracking, production optical/IMU fusion, PS Move transport, button mapping or screen-corner wizard is implemented here. The relative attitude display is a separate diagnostic.

The adapter currently accepts only a PS Eye tracker reporting 640×480. Stop capture before reconfiguring the service. The public SDK returns a frame pointer with no payload length; this version requires the configured fixed VGA BGR mode and checks reported tracker width before reading it. Supporting other modes requires a dimension-aware SDK extension rather than guessing buffer lengths.

The private child pipe has a 32-byte little-endian header: magic `0x31455950`, version `1`, width `640`, height `480`, stride `1920`, payload `921600` (six 32-bit integers), then a signed 64-bit QPC timestamp; packed BGR data follows. Managed code validates dimensions before reading the bounded payload. This pipe is internal to the adapter/preview and is not PSMoveServiceEx's network protocol.

## Local validation (2026-10-31)

The PSMoveServiceEx-bundled PS Eye video driver was installed with user approval (installer exit 0, Windows service `libusb0`, device ConfigFlags 0). Audio retains `usbaudio`. The real SDK discovered one 640×480 PS Eye and successfully received shared-memory video after correcting the working-directory requirement.

Measured 120-frame runs: requested 30 fps delivered 30.1 fps with a purple target in 120/120 frames; requested 60 delivered 58.5 fps with 120/120 detections; requested 75 delivered 67.2 fps with 106/120 detections. These short stationary tests do not establish moving-target reliability or end-to-end latency. The visible bar is near the bottom edge of the camera image and needs more tracking space for movement. The workspace service configuration currently requests 75 fps; camera intrinsics/extrinsics have not been calibrated.

The DS4Windows x64 Release regression suite previously passed 7,205 tests (26 opt-in skips). The diagnostic now has 46 synthetic checks and the eight-frame synthetic pipe check; the main application was unchanged by the 3D preview. Live wired DSU and the renderer were verified. The service-pose audit's no-public-DS4 path was exercised; its Bluetooth pose-stream branch remains untested. Cursor output and profile integration are not implemented in this stage.

Profile integration is the next stage: reuse `Mouse` activation, `MouseCursor`/`Global.outputKBMHandler`, serialization and callback-retirement rules. The capture callback must not inject input; camera creation must be opt-in and profile/controller changes must retire old samples.
