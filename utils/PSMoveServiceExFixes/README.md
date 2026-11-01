# Native PSMoveServiceEx DS4 fixes

This is a patch bundle against **v0.27.8, commit `308cb9ea55dc1ed32b2c0d60e63d73b1b3c5d9f1`**. It is a modified build, not an official upstream release. Original upstream source and SDK license: Apache-2.0. Dependency notices remain applicable.

## Behavioral fixes

- Include DS4 in Config Tool's controller list; its filter controls already exist but were unreachable. Also expose the existing complementary optical filter's inherited Madgwick/stabilization controls.
- Persist per-axis gyro bias in raw counts; subtract before conversion to rad/s. Apply the bias received by the standard gyro calibration request. Convert its raw-count noise/drift statistics into SI units. Gain-only requests preserve bias/noise instead of storing their negative sentinel values.
- Preserve independent optical orientation validity through the internal filter packet. Both complementary and optical-only DS4 orientation filters reject position-only packets rather than blending toward an invented identity rotation. Position tracking can continue independently.
- Align quaternion hemispheres before normalized interpolation, including antipodal equivalent rotations and wraparound near 360 degrees.
- Restore DS4 gyro/accelerometer calibration controls and optical-noise calibration. Initialize controller/tracker selection explicitly, accept the visible tracker, and return to Controller Settings. DS4 testing controls are no longer hidden solely by transport type; USB sensor availability remains subject to the existing native limitation.
- Render the actual DS4 in the orientation test instead of an unconditional PS Move model. Keep PS Move rendering for virtual emulation, identify gyro readouts as degrees/s, and wrap the recenter instructions. Add a separate optical-tracking test that does not sample or save noise curves.
- Repair the newly reachable optical-noise workflow: collect only on updated controller packets at no more than 33 Hz, require finite samples, floor zero sample variances before logarithmic fitting, reject invalid/increasing/underflowing curves or less than 20% projection-area spread, and save only after an explicit action with service acknowledgement. Cancel before saving preserves the existing curves. The tooltip now gives the real menu path and explains that defaults apply until measured.


The optional stationary gyro-bias learner is described in [AUTO_CALIBRATION.md](AUTO_CALIBRATION.md). It adjusts sensor bias, not the displayed pose or a stationary lock. No new EKF, heading-only filter, or TV alignment is introduced. Bluetooth DS4 is tested; the earlier USB fusion limitation remains.

## Apply and test

Clone the exact revision, initialize its submodules, then run:

```powershell
python utils/PSMoveServiceExFixes/apply-patches.py PATH/TO/PSMoveServiceEx
cmake -S utils/PSMoveServiceExFixes/tests -B native-tests -A x64 "-DPSM_SOURCE=C:/absolute/path/to/PSMoveServiceEx"
cmake --build native-tests --config Release --parallel 4
ctest --test-dir native-tests -C Release --output-on-failure
```

The patch manifest pins each affected dependency revision. The applier rejects unexpected revisions/conflicting source and recognizes already-applied patches. Application and repeat application were verified on fresh worktrees. Regression tests compile the actual upstream filter/math code, exercising accepted/rejected optical correction and sign-safe quaternion interpolation. They do not connect hardware or install drivers.

`calibrate-ds4.cpp` is a small client for the existing service calibration request, with explicit request acknowledgement. Its raw bias/noise inputs must come from stationary recordings. `id --gain-only` explicitly selects the 2000 deg/s gain and tests calibration preservation; it is not a no-op gain inspection.

## Full native build

The upstream CMake files and pinned dependencies were not sufficient for a current Windows build. The bundle includes separate build compatibility changes: current version-header parsing, explicit standard/Windows includes and libraries, modern Asio compatibility, OpenCV scalar calls, and optional exclusion of obsolete upstream test targets referencing removed files. Dependency patches supply the camera frame API and selected-tab helper expected by the service but absent from its pinned dependencies, plus current-compiler fixes for protobuf. The camera frame API supports nonblocking reads and bounded waits.

The tested local build uses x64 MSVC 19.44, Windows SDK 10.0.26100, CMake 3.27.9, Ninja 1.12.1, Boost 1.78.0 (static runtime), OpenCV 3.4.20, the pinned protobuf and SDL source revisions, and prebuilt x64 VS2015 libusb 1.0.30. These dependency changes mean behavior comparisons are not isolated algorithm-only experiments.

The dependencies are built inside the workspace's `.tools` directory:

1. Boost: build `atomic chrono filesystem program_options system thread date_time` with x64 MSVC, release, multithreaded, static linkage and static runtime; stage into `boost_1_78_0/stage/lib`.
2. Protobuf: configure its patched `cmake` directory into `build-protobuf`, Release/Ninja, `protobuf_BUILD_TESTS=OFF`, `protobuf_MSVC_STATIC_RUNTIME=ON`; build the libraries and protoc.
3. SDL: configure its pinned directory into `build-sdl`, Release/Ninja, `SDL_SHARED=OFF`, `SDL_STATIC=ON`, `FORCE_STATIC_VCRT=ON`, `DIRECTX=OFF`, `LIBC=ON`, `CMAKE_C_FLAGS=/MT /DWIN32 /D_CRT_SECURE_NO_WARNINGS /DHAVE_LIBC`; build.
4. OpenCV: configure tag 3.4.20 into `build-opencv`, Release/Ninja, static CRT/libraries, with `core,imgproc,imgcodecs,calib3d,features2d,flann,highgui,videoio,video,ml,objdetect,photo`. Disable tests/examples/apps, Python/Java bindings, IPP, FFMPEG, DirectX/OpenCL D3D11 interop, MSMF, DirectShow, OpenEXR, Jasper, TIFF and WebP. Build. Capture uses PS3EYEDriver/libusb, not those disabled video backends.
5. Supply libusb headers and `libusb/VS2015/MS64/static/libusb-1.0.lib`. The compiler environment needs the SDK's legacy shared headers as well as the desktop headers; the local portable extraction supplemented missing headers from Microsoft's win32metadata source.

Then, from a VS x64 developer shell or the local `.tools/native-env.ps1` environment:

```powershell
utils/PSMoveServiceExFixes/build-native.ps1 -PsmSource PATH/TO/PSMoveServiceEx -DependencyRoot PATH/TO/.tools -BuildDirectory PATH/TO/.tools/build-psm
```

Use the rebuilt `PSMoveClient_CAPI.dll` with the rebuilt service/UI. To package the preview against it, pass `-ClientDll PATH/TO/PSMoveClient_CAPI.dll` to its existing `build.ps1`. This matters for shared-memory compatibility across Boost builds.

## Local deployment and rollback

The working local installation is `.tools/PSMoveServiceEx/patched/PSMoveService`; its separate configuration is `.tools/PSMoveServiceEx/patched-data/PSMoveService`. `configuration-path.txt` identifies that directory to the preview. The original `.tools/PSMoveServiceEx/service` and `.tools/PSMoveServiceEx/data` remain intact.

The desktop UI launcher chooses the patched installation after its readiness marker exists, starts the service if necessary, then opens the tool labeled **DS4 fixes**. Close the UI and run `.tools/PSMoveServiceEx/Start-UI.ps1 -Original` to switch back. The launcher only switches between its two known local service installations. The Controller Tracking shortcut uses the same launcher with `-Tracking`.

## Live validation, 2026-10-31

The user confirmed that the patched native UI now lists DualShock4 and displays its filter controls. Live camera validation delivered 120/120 detections at 58.2 fps. Native service audit delivered 642 valid poses from 648 fresh states, with startup acquisition included.

A current stationary calibration reduced mean gyro vector magnitude from **0.1216 to 0.0156 deg/s (87.1%)**. Calibration was acknowledged through the service API and persisted; a gain-only request preserved bias/noise. Fused angular RMS was about **0.43 deg before bias calibration and 0.48 deg afterward**; this run does not demonstrate improved fused stability or elimination of drift. Earlier much larger excursions occurred under different conditions. Optical noise, camera/body alignment, thermal bias changes, motion, and occlusion still need validation before TV pointing can be certified. Raw recordings remain local in `artifacts/FusionAudit/`.

The versioned `Start-UI.ps1` is the launcher source; deploy it into the local `.tools/PSMoveServiceEx` directory to use the installation layout above.

## DS4 calibration/testing UI follow-up

The user confirmed the deployed native UI now shows Gyroscope, Accelerometer and Optical Noise under DS4 Calibration, and Test Orientation renders the DS4. Full native Config Tool build and both native regression executables pass; additional optical-curve tests cover normal/constant curves, inadequate coverage, zero/nonfinite/increasing coefficients and numerical underflow. The exported patch also passes `git apply --check` against a clean pinned source worktree, and reverse verification recognizes the installed changes.

Only the patched Config Tool binary was replaced for this follow-up; the running service was preserved. Existing accelerometer, rumble and button tests retain their behavior. No physical optical-noise calibration session was performed and no new noise curves were automatically applied. Recalibration/recentering can change the tracking reference; record a new TV reference afterward. These changes belong to PSMoveServiceEx; the DS4Windows fork currently carries the reproducible native patch bundle.
