# SDK adapter notices

The new DS4Windows SDK adapter, preview and tracking code are GPL-3.0-or-later; see repository `COPYING`, packaged as `DS4Windows-COPYING.txt`.

The adapter builds against official **PSMoveServiceEx v0.27.8 PSMoveClient_CAPI** headers and dynamically loads its x64 DLL. Source and SDK releases: [Timocop/PSMoveServiceEx](https://github.com/Timocop/PSMoveServiceEx/tree/v0.27.8), [release](https://github.com/Timocop/PSMoveServiceEx/releases/tag/v0.27.8). Retain the SDK's Apache-2.0 `LICENSE.txt`, copied into packages as `PSMoveServiceEx-LICENSE.txt`. No SDK binary is committed to this repository. The independently installed PSMoveServiceEx service has its own third-party dependencies and notices; this adapter neither replaces nor rebundles its camera driver.

Zig 0.14.1 is a build-time compiler and is not bundled. No opentrack/PS3EYEDriver or direct libusb dependency is part of this adapter.

The local DS4-fixes deployment uses a rebuilt client DLL matching the patched service, rather than the unmodified SDK DLL. Its pinned source patches and build notes are in `utils/PSMoveServiceExFixes/` in this fork. Pass `-ClientDll PATH` to `build.ps1` to package that matching DLL. Retain the Apache-2.0 notice; the native service/UI package also retains its own third-party notices.
