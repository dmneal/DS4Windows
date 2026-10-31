# Automatic DS4 gyro bias learning

## Research

Valve's [June 2023 client release notes](https://store.steampowered.com/news/posts/?enddate=1689224699) document changing gyro calibration to a five-second time window because sample-count windows became too short at high report rates. Valve also documents [anti-drift calibration tolerances](https://store.steampowered.com/news/posts/?enddate=1701713393&feed=steam_blog). These notes describe behavior, not the internal estimator; no Steam implementation is copied or claimed here.

[GamepadMotionHelpers](https://github.com/JibbSmart/GamepadMotionHelpers) provides inspectable stillness and gravity-based bias correction. Its [implementation](https://github.com/JibbSmart/GamepadMotionHelpers/blob/main/GamepadMotion.hpp) evaluates gyro/accelerometer variation, averages quiet samples, and eases bias updates. Its author explains that slow deliberate motion can be mistaken for stillness, and gravity alone cannot resolve rotation about gravity. We retain manual calibration and an off switch. The implementation here is independent; this is not a vendored library or a new orientation fusion filter.

## Current behavior

Open the tracking preview's **Gyro calibration** tab. Changes are saved to `gyro-autocalibration.json` in the selected service working directory and read by the patched service once per second. Learning is disabled if the file is absent or invalid; the local installed configuration explicitly enables it.

The default stillness decision uses **IMU only**, per Devin's live feedback. Optical jitter, rejected optical orientation, and camera loss do not reset that window. The camera continues its normal role in pose fusion. An optional **Also require stable camera orientation** gate is available separately.

For each controller, on the service main thread before fusion:

1. Collect five uninterrupted seconds of raw gyro and calibrated accelerometer samples. Reject invalid values, non-increasing timestamps, IMU gaps over 100 ms, large rotation spikes, or acceleration magnitude more than 0.08g from 1g.
2. Require mean residual gyro vector magnitude at most 0.08 degrees/s, per-axis gyro standard deviation at most 0.25 degrees/s, and acceleration standard deviation at most 0.015g. The instantaneous rate check includes four standard-deviation tolerances so ordinary sensor noise does not repeatedly restart a quiet window.
3. Estimate bias from the window's mean raw angular velocity. Move 25% toward it, capped at 0.01 degrees/s vector magnitude per accepted window and 0.25 degrees/s total from the session's starting bias. This learns offsets only; gyro gain/noise settings remain unchanged.
4. Apply through the controller's existing configuration exchange, and recompute queued raw IMU packets with the latest bias before fusion. Retain updates in memory; persist on an accepted window at most once every 60 seconds. Recent unsaved changes can be lost on exit. Pausing learning retains the current bias. Manual bias/gain calibration resets the automatic session baseline.

The optional camera gate requires fresh valid optical orientation, at least 50 optical observations, and compares quaternion/position averages from the first and last quarters. Defaults are 1.25 degrees and 0.01 meters. Slight cross-batch camera/IMU timestamp skew is tolerated. This gate can reject real stillness in this single-camera setup, so it is off by default.

Live status is written atomically once per second to `gyro-autocalibration-status-ID.json`: decision, last reset reason, window progress, longest observed window, accepted window count, time since last update, and current bias. The preview marks status older than three seconds stale. No fresh IMU means no learning.

## Validation and limits

Native regression tests cover time windows at 100/800 Hz, noise, IMU-only operation without camera tracking, optional camera-loss rejection, deliberate 0.2 degrees/s yaw, fast movement, correction limits, invalid samples, and gaps. Existing quaternion/optical-validity regressions also pass. The full service and preview build; the 66 existing C# checks and synthetic frame pipeline pass.

The replay tool accepts CSV telemetry without touching hardware. Replaying our previously recorded 30-second calibrated stationary dataset with the final IMU-only settings accepted five windows; the early optical-gated/low-noise-tolerance version accepted none. Replay uses received network samples rather than the full native IMU rate, so it complements live testing rather than replacing it. Raw room/device recordings remain local.

Very slow steady yaw below the mean-rate threshold can still be absorbed as bias in IMU-only mode. The bounds limit the effect; they do not solve this observability limit. Disabling automatic learning and manually calibrating remains available. No reduction in live fused orientation drift is claimed from these tests, and USB native fusion is still unvalidated.

The deployed IMU-only service subsequently completed full five-second windows and reported six accepted live bias updates. The status snapshot is retained locally in `artifacts/FusionAudit/imu-auto-calibration-live-status.json`. A camera initialization failure during a rapid service restart cleared on retry; the final service and preview are running. Live moving-controller validation remains separate from this stationary acceptance check.
