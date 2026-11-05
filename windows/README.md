# Building TinyHand for Windows

Installing and using the app is covered in the [main README](../README.md). This page is for building and
releasing it.

TinyHand for Windows is C#/.NET 10 with WPF for the windows, Direct2D and DirectComposition for the pointer
overlay, low-level mouse and keyboard hooks for catching clicks, XAudio2 for the tap sound and Velopack for
installing and updating.

## Layout

- `src/TinyHand.Core`: the arm motion, ink smoothing, hand measuring and custom-hand checks, ported from the
  Swift app. Plain .NET, so it builds and tests on any OS.
- `src/TinyHand`: the Windows app. `Overlay/` draws and runs the pointer, `Input/` holds the hooks and the
  click-through zones, `Tray/` holds the tray icon and shortcuts, and `UI/` holds the toolbar, welcome card
  and Settings.
- `tests/TinyHand.Core.Tests`: unit tests for the core, run against the real hand images.
- The hand images and sounds come from the shared `assets/` folder at the top of the repo.

## Build and run

Needs the .NET 10 SDK. The core and its tests also build on macOS and Linux.

```powershell
dotnet test windows/tests/TinyHand.Core.Tests
dotnet publish windows/src/TinyHand -c Release -r win-x64 --self-contained -o out/win-x64
out/win-x64/TinyHand.exe
```

`TinyHand.exe --self-test <folder>` renders the pointer and every screen to PNGs, turns the pointer on,
clicks, drags and holds Alt with synthetic input, and writes `report.txt`. Add `--no-input` to skip the
input part.

## CI

Every push to the `windows` branch runs the [Windows workflow](../.github/workflows/windows.yml): unit tests,
x64 and ARM64 builds, the self-test on a Windows runner (its screenshots and report are uploaded as the
`selftest` artifact), and the installer and portable zips (the `TinyHand-windows` artifact).

## Release

Bump `windows/VERSION`, commit, then push a tag such as `win-v1.0.1`. The workflow publishes a GitHub release
with the installer, and installed copies update from it.
