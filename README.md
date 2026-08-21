# Dark Ages windower

A C# windower for the Dark Ages client.

## Features

- **Client compatibility**
  Supports clients from 2.x up to 7.x.
- **Resizable window**
  Drag-resizing is aspect-locked (configurable) and magnet-snaps to integer scales (1×/2×/3×…)
  when the drag lands near one, for crisper rendering.
- [**Borderless fullscreen**](#borderless-fullscreen)
- [**Flickering cursor fix**](#cursor-flicker-fix)
  No more flickering cursor in 5.x and later clients.
- [**Rainy weather (only for 7.41)**](#rainy-weather)
  Re-creates the rainy weather effect.
- [**Map overlay (only for 7.41)**](#map-overlay)
  View a scaled-down version of the current map.
- **Multi-instance**
- [**Intro skip**](#intro-video-skipped-by-default)

## Usage

Used in one of two ways, as a proxy for DirectDraw or injected into the game's process at startup.

### 1. DirectDraw proxy `ddraw.dll`

No injection required and generally less hated by anti-virus software.
The client imports `DirectDrawCreate` from `ddraw.dll`, so the payload can stand in as `ddraw.dll`.
The game loads it through the normal DLL search order.

```
Darkages.exe
ddraw.dll        <- renamed DawndNet.dll
DawndNet.ini     <- optional settings (see below)
```

Run `Darkages.exe` normally. Options come from `DawndNet.ini`. See [Settings file](#settings-file-optional).

### 2. Injector

Launches the game and loads the payload itself. Useful when you want to pass arguments or pick a
custom executable without an ini, or when something else already occupies `ddraw.dll`.
Can be run from outside of the game's own folder.

```
Darkages.exe
DawndNet.exe     <- run this
DawndNet.dll
DawndNet.ini     <- optional settings (see below)
```

## Injector command-line arguments

Any argument that is not a supported setting is forwarded verbatim to the game, so
older clients can, for example, be given server info:

```
DawndNet.exe 127.0.0.1 2610              -> Darkages.exe 127.0.0.1 2610
DawndNet.exe --borderless 127.0.0.1 2610 -> Darkages.exe 127.0.0.1 2610 in borderless fullscreen
```

The injector uses the same keys as the [Settings file](#settings-file-optional). Keys provided to the injector will override values from the settings file.

Usage: `--borderless=true` / `--borderless=false`, and a bare `--borderless` means `=true`.
 - `--borderless` (off by default)
 - `--keepintro` (skipped by default)
 - `--lockaspect` (on by default)
 - `--cursorfix` (on by default, auto-limited by client)
 - `--rain` (off by default)
 - `--map` (on by default, see [Map overlay](#map-overlay))
 - `--scale=<1-2>` (1 by default, see [Window scale](#window-scale))
 - `--scalingmode=<0-1>` (0 by default, see [Scaling mode](#scaling-mode))
 - `--exe <path>` (or `--exe=<path>`)
 - `--ignoreini`

## Settings file (optional)

`DawndNet.ini` (next to the executable).

- **Injector mode:** read as the **base layer**, then any key on the command line overrides the matching ini key. Pass `--ignoreini` to skip the file entirely and start from the built-in defaults.
- **Proxy mode (`ddraw.dll`):** the payload reads the file itself from the game's folder.

```ini
# One "key=value" per line.
borderless=false
keepintro=false
lockaspect=true
cursorfix=true
rain=false
map=false
scale=1
scalingmode=0
#exe=C:\Dark Ages\Custom_Darkages.exe
#args=127.0.0.1 2610
```
`args` are appended verbatim to the game command line.

### Window scale

`scale` is the integer multiple of the 640x480 image the window starts at:

| `scale` | Window client area |
| ------- | ------------------ |
| `1`     | 640x480 (default)  |
| `2`     | 1280x960           |

### Borderless fullscreen

The window becomes a caption-less popup filling the monitor the client opened on, and
the 640x480 image is centered and scaled up preserving its 4:3 aspect ratio, with black
bars on the sides. How the image is scaled into that rectangle is set by
[`scalingmode`](#scaling-mode).

**`Alt+Enter` toggles it at runtime**, whichever way `borderless` started. Going
borderless remembers the window's frame and returns it there; if the client started
borderless there is nothing to restore, so the first toggle out builds a window at the
configured [`scale`](#window-scale) and centres it on the monitor. The key is swallowed,
so the client never sees it.

The toggle only applies once the windower has taken over the client's fullscreen. A
client already running in its own native windowed mode keeps its own frame, and
`Alt+Enter` does nothing.

### Scaling mode

`scalingmode` picks how the 640x480 image is fitted.

| `scalingmode` | Name      | Behaviour |
| ------------- | --------- | --------- |
| `0`           | `fill`    | Aspect-preserved and as large as fits, point sampled. Default. |
| `1`           | `integer` | The largest whole multiple that fits, black bars on all four sides. |

The names are accepted in place of the numbers, so `scalingmode=integer` also works.

### Intro video

The intro Bink video (`CIb.bik` / `CIf.bik`) is played through
`binkw32!BinkOpen`. The payload IAT-hooks that import and sets the returned video's
frame count to 1 (`BINK.Frames` at ABI offset +0x08), so playback ends on the first
frame and the client's intro pane advances immediately.

### Cursor-flicker fix

Clients from **5.x onward** draw their mouse cursor as a separate blit *after* the
scene, which on modern Windows lets a present catch the frame with
the cursor mid-draw, so it flickers as it moves. The payload fixes this by **coalescing
presents to the frame boundary** instead of presenting on every blit, it marks a
present pending and flushes it when the client next reads its message queue (which only
happens between finished frames).

This is **auto-limited to the clients that need it**, the ones presenting via `BltFast`
(5.x+). Older clients (≤4.x) present via `Blt`/`Unlock` and have no such flicker.
The detection keys off the present path (`BltFast` vs `Blt`/`Unlock`), so it stays
version-address-independent. `--cursorfix=false` (or `cursorfix = false` in the ini)
forces immediate present for every client.

### Rainy weather
***Only for client 7.41***

Re-implements the rainy weather effect through the newer snow particle system.
Use **F11** to force-toggle rain, or enter a rainy map.

### Map overlay
***Only for client 7.41 and extremely experimental***

Press **F2** for a scaled-down render of the current map.

## Build

Requires the .NET 10 SDK and the Visual Studio C++ (x86) build tools. Everything is 32-bit because the client is a 32-bit process.

The payload also contains one C file, `src/Payload/early.c`, which is compiled by the build and linked into `DawndNet.dll`.
It exists because Native AOT does not run managed code while the DLL is loading, and the payload needs to install a few hooks before
the game's `WinMain`. `vswhere.exe` must be on `PATH` so the build can find the x86 `cl.exe`, which the native link step already requires.

Publish the whole solution:

```sh
dotnet publish DawndNet.slnx -c Release -r win-x86
```

Outputs land in each project's `bin/x86/Release/net10.0/win-x86/publish/` (`DawndNet.dll` and `DawndNet.exe`).

Or publish the projects individually:

```sh
dotnet publish src/Payload/Payload.csproj   -c Release -r win-x86
dotnet publish src/Injector/Injector.csproj -c Release -r win-x86
```

Add `-p:DAWND_LOG=false` to compile out all `OutputDebugString` calls.

## Notes

Referenced https://github.com/ewrogers/darkages-741-re for client specifications.