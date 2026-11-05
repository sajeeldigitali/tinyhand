<img src="./.github/logo.png" width="80" />

# TinyHand for Windows

[![Platform: Windows 11](https://img.shields.io/badge/platform-Windows%2011-0078D4)](#install)
[![Windows build](https://github.com/sajeeldigitali/tinyhand/actions/workflows/windows.yml/badge.svg?branch=windows)](https://github.com/sajeeldigitali/tinyhand/actions/workflows/windows.yml?query=branch%3Awindows)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)

A tiny hand on a stick that points at things on your Windows screen.

<img src="./.github/banner.jpg" alt="TinyHand: a tiny hand on a stick pointing at the screen" />

This branch is a Windows 11 port of [TinyHand](https://github.com/nilbuild/tinyhand), the macOS app by
[@nilbuild](https://x.com/nilbuild). It has the same 36 hands, laser dot, drawing, tap sounds and settings,
rebuilt for Windows. Free, and MIT licensed like the original.

Windows 11, x64 and ARM64.

## Install

1. Open the [latest Windows build](https://github.com/sajeeldigitali/tinyhand/actions/workflows/windows.yml?query=branch%3Awindows)
   (you need to be signed in to GitHub), pick the newest run and download **TinyHand-windows** under Artifacts.
   Builds published as [releases](https://github.com/sajeeldigitali/tinyhand/releases) can be downloaded directly.
2. Unzip it, then either:
   - run **TinyHand-win-Setup.exe**. It installs for you only (no admin), adds TinyHand to Start, opens it when
     you sign in and updates itself, or
   - unzip **TinyHand-win-x64.zip** (**-arm64** on Snapdragon PCs) anywhere and run `TinyHand.exe`.
3. The app isn't code-signed, so Windows may say "Windows protected your PC". Click **More info → Run anyway**.

TinyHand lives in the system tray, next to the clock. Windows 11 hides new tray icons, so if you can't see the
hand, click **^** next to the clock and drag it onto the taskbar.

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

While the pointer is on, a small toolbar at the bottom of the screen lets you pick one of 36 hands or a laser
dot, change the ink colour, mute the tap sound, or exit. Clicks on the taskbar and on open menus stay real.

Settings (right-click the tray icon) lets you change the shortcuts and the click-for-real key, pick which hands
sit in the toolbar, make your own hand from an image, and set the size, shadow, ink colour, fade time and sound.

## Differences from the Mac version

- No permission prompt: Windows doesn't need Accessibility access.
- Hold **Alt**, **Ctrl** or **Shift** (your choice, Alt by default) to click for real. Alt+Tab and other
  shortcuts still work while you hold it.
- The hand can't appear above the Start menu, Task Manager or apps running as administrator, and clicks on
  those stay real.
- Touchscreen and pen taps aren't caught, only the mouse and touchpad.

## Problems

Settings → About → **Show log file** opens `%AppData%\TinyHand\TinyHand.log`. Send it along with what happened.
If the mouse cursor ever goes missing, start TinyHand again, or sign out and back in.

## Build

See [windows/README.md](windows/README.md). The original Swift source for macOS is still in `Sources/`.

## License

MIT. See [LICENSE](LICENSE). TinyHand was created by Kamran Ahmed; this is an unofficial Windows port.
