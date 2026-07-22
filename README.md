# DawndNet

A C# windower for the Dark Ages client.

## Features

- **Client compatibility**
  Supports clients from 2.x up to 7.x.
- **Resizable window**
  Drag-resizing is aspect-locked (configurable) and magnet-snaps to integer scales (1×/2×/3×…)
  when the drag lands near one, for crisper rendering.
- [**Borderless fullscreen**](#borderless-fullscreen)
  Run the game in a 4:3 letterboxed fullscreen mode.
- **Multi-instance**
  Run several clients at once.
- [**Flickering cursor fix**](#cursor-flicker-fix)
  No more flickering cursor in 5.x and later clients.
- [**Intro skip**](#intro-video-skipped-by-default)
  No annoying video when starting the client.

## Two ways to run it

### 1. Injector — recommended for guaranteed early hooks

Use this to access all features.

```
Darkages.exe
DawndNet.exe     <- run this
DawndNet.dll
```

### 2. Proxy `ddraw.dll` — no injection, AV-friendly

The client imports `DirectDrawCreate` from `ddraw.dll`, so the payload can stand in as `ddraw.dll`.
The game loads it itself through normal DLL search order.
There is no need for the injector and no process injection at all.

**Important**: The client's display mode must be set to `Full Screen Mode` using `DA-DisplaySelector.exe`.

```
Darkages.exe
ddraw.dll        <- rename DawndNet.dll to this
DawndNet.ini     <- optional settings (see below)
```

Run `Darkages.exe` normally. Options come from `DawndNet.ini`. See [Settings file](#settings-file-optional).

**Limitations**: `DisplayMode` hooks install when `DirectDrawCreate`
first runs, which is after the client's early startup. Because of this, **multi-instance** and
**intro skip** will not function.

If you want these features you'll have to patch your game client by yourself.

### Forwarding arguments to the game

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
 - `--exe <path>` (or `--exe=<path>`)
 - `--ignoreini`

### Settings file (optional)

`DawndNet.ini` (next to the executable).

- **Injector mode:** read as the **base layer**, then any key on the command line overrides the matching ini key. Pass `--ignoreini` to skip the file entirely and start from the built-in defaults.
- **Proxy mode (`ddraw.dll`):** the payload reads the file itself from the game's folder.

```ini
# One "key=value" per line.
borderless=false
keepintro=false
lockaspect=true
cursorfix=true
#exe=C:\Dark Ages\Custom_Darkages.exe
#args=127.0.0.1 2610
```
`args` are appended verbatim to the game command line.

### Borderless fullscreen

```
DawndNet.exe --borderless
```

The window becomes a caption-less popup filling the primary monitor, and the 640x480
image is centered and scaled up preserving its 4:3 aspect ratio, with black bars on
the sides.

### Intro video (skipped by default)

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

## Build

Requires the .NET 10 SDK and the Visual Studio C++ (x86) build tools. Everything is 32-bit because the client is a 32-bit process.

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
