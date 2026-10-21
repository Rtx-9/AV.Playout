# AV Playout

Lightweight live-event playout for venue LED boards. C# / WPF, .NET 10, Windows x64.

**Phase 1:** dual-window foundation. A control dashboard drives a borderless
output window pinned to the LED display. Click a colour swatch or a still in the
bin and the wall changes on the next frame.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design and roadmap.

## Download

Grab `AV.Playout.exe` from the [Releases](../../releases) page. It is a single,
self-contained file for Windows 10/11 x64: no .NET install needed. Windows
SmartScreen may warn on first launch because the exe is not code-signed; choose
**More info → Run anyway**.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet run --project src/AV.Playout
```

Single-file exe:

```
dotnet publish src/AV.Playout -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Or open `AV.Playout.slnx` in Visual Studio 2022 17.13+ / Visual Studio 2026.

## Using it

1. Pick the LED output in **Output display** (the first non-primary display is
   pre-selected). On a single-screen machine, **Windowed (rehearsal)** is ticked
   for you.
2. Click **OUTPUT OFF** to go live. It turns red.
3. Click a colour swatch to change the background, or add stills (button or
   drag and drop) and click one to put it on air. The on-air still gets a red
   tally frame.
4. **Ctrl+F12** pulls the output instantly, even if it is covering the control
   window.

Set the program raster (`CanvasWidth` / `CanvasHeight`) in `App.xaml.cs` to match
your LED processor's input.
