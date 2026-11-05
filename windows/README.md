# TinyHand for Windows

A Windows 11 version of TinyHand: a tiny hand on a stick that points at things on your screen. It's a
port of the macOS app in this repository, rebuilt for Windows with C#/.NET 10, WPF, Direct2D and
DirectComposition. It uses the same 36 hands, laser dot, sounds and settings.

Windows 11 only (x64 and ARM64).

## Install

From the latest run of the **Windows** workflow on the `windows` branch (Actions tab → Windows →
the newest run → **TinyHand-windows** artifact), either:

- run **TinyHand-win-Setup.exe**. It installs for you only (no admin), adds TinyHand to Start, opens it at sign-in and updates itself from this repo's `win-v*` releases, or
- unzip **TinyHand-win-x64.zip** (or `-arm64` on Snapdragon PCs) anywhere and run `TinyHand.exe`.

The app isn't code-signed, so Windows SmartScreen may say "Windows protected your PC". Click
**More info → Run anyway**.

TinyHand lives in the system tray. Windows 11 hides new tray icons, so if you can't see the hand,
click **^** next to the clock, or turn it on in Settings → Personalization → Taskbar → Other system tray icons.

## Use

| | |
|---|---|
| Click the tray icon, or **Ctrl+Alt+H** | Turn the pointer on or off |
| Right-click the tray icon | Menu and Settings |
| Click | Tap (nothing underneath gets clicked) |
| Hold and drag | Draw on the screen |
| Hold **Alt** and click | Click for real |
| **Ctrl+Alt+X** | Clear the drawing |
| **Esc** | Clear everything and turn the pointer off |

While the pointer is on, a small toolbar at the bottom of the screen lets you pick a hand or the
laser dot, change the ink colour, mute the tap sound, or exit. Clicks on the taskbar and on open
menus stay real, like the menu bar on the Mac.

## How it differs from the Mac

- No permission prompt: Windows doesn't ask for Accessibility access.
- "Click for real" uses Alt, Ctrl or Shift (default Alt) instead of ⌥, ⌘ or ⌃. Alt+Tab and other
  shortcuts still work while you hold it.
- The hand can't appear above the Start menu, Task Manager or windows of apps running as
  administrator, and clicks on those windows stay real. Fixing that needs a signed build installed
  under Program Files.
- Touchscreen and pen taps aren't caught, only the mouse and touchpad.

## Problems

Settings → About → **Show log file** opens `%AppData%\TinyHand\TinyHand.log`. Send that file
along with what happened. If the mouse cursor ever goes missing, start TinyHand again, or sign out
and back in. TinyHand restores the cursor on its next start.

## Build

Needs the .NET 10 SDK.

```powershell
dotnet test windows/tests/TinyHand.Core.Tests
dotnet publish windows/src/TinyHand -c Release -r win-x64 --self-contained -o out/win-x64
out/win-x64/TinyHand.exe
```

`TinyHand.exe --self-test <folder>` renders the pointer and every screen to PNGs, turns the pointer
on, clicks and drags with synthetic input and writes `report.txt`. The Windows workflow runs it on
every push.

The code is in `windows/src/TinyHand.Core` (the arm, ink and custom-hand maths, testable anywhere)
and `windows/src/TinyHand` (the Windows app). The hand images and sounds come from the shared
`assets/` folder.

## Release

Bump `windows/VERSION`, then push a tag such as `win-v1.0.1`. The workflow builds the installer and
publishes a GitHub release that installed copies update from.
