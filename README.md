# AV Playout

Live-event playout for venue LED boards and scoreboards, in the spirit of
Resolume Arena / JD-Playout. C# / WPF on .NET 10, Windows 10/11 x64.

- **Clip grid** with three layers: MEDIA (sponsor loops, hype videos, stills),
  LIVE (camera, capture card, network stream) and GRAPHICS (alpha clips + scorebug)
- **Crossfades** on every trigger, layer opacity faders, bypass, master fader, BLACKOUT
- **Preview / Program**: right-click cues a clip pre-buffered on its first frame; TAKE plays it instantly
- **Takeovers**: one key plays a "Home Goal" clip over everything, pulls the live
  feed, keeps the scorebug up, then restores whatever was on air
- **Global hotkeys** for USB macro keypads: fire clips even when another app has focus
- **Scorebug** with game clock (mm:ss → ss.t in the last minute), periods, scores and
  penalty clocks that run with the game clock
- **Text graphics**: live-typed lower third, scrolling ticker, crowd prompts ("MAKE SOME NOISE") with auto-hide
- **Sponsor rotation**: MEDIA clips cycle automatically with per-clip dwell times
- **Proof-of-play log**: every play written to a daily CSV, with today's airtime per clip in the app
- **Countdowns**: time remaining and a progress bar on the playing clip, its layer and the program monitor; red in the last 5 s
- **Venue visualizer**: live 3D arena (centre-hung cube, wrap-around ribbon board, end board) showing
  exactly what each screen shows, with an editable map of which canvas region feeds which screen
- **Output** pinned pixel-exact to the LED display; auto-assigned to the secondary display at startup
- **Show file** autosaves the grid, hotkeys, rotation, text and screen layout

## Download

Grab `AV.Playout.exe` from the [Releases](../../releases) page: one self-contained
file, no .NET or VLC install needed. Windows SmartScreen may warn on first launch
because the exe is not code-signed; choose **More info → Run anyway**.
The first launch takes a few seconds longer while it unpacks.

## Using it

| Action | How |
|---|---|
| Load media | Drag files onto a slot (several files fill the row), or double-click an empty slot |
| Trigger to program | Left-click a slot, press its hotkey, or click a column number to fire the whole column |
| Cue to preview | Right-click a slot, then **TAKE ▸** |
| Clip mode | Select a slot → **LOOP / ONCE / TAKEOVER** |
| Hotkey | Select a slot → **ASSIGN** → press a key. Program your macro keypad to send **F13–F24**: no other app uses them |
| Live input | Select a slot on the LIVE row → **CAMERA** (`dshow://`), or type `rtsp://…`, `srt://…`, `udp://…` and press Enter |
| Scorebug | **SCOREBOARD** tab → names, goals, clock, penalties, **SHOW ON OUTPUT** |
| Lower third / ticker / prompts | **TEXT** tab → type, then **SHOW** / **START**; prompt presets fire with one click |
| Sponsor rotation | **ROTATION** tab → tick **IN** on MEDIA clips, set seconds (0 = play a video to its end), **START ROTATION** |
| Proof of play | **AS-RUN** tab → today's airtime per clip; **OPEN LOG FOLDER** for the CSVs |
| Venue preview | **VENUE 3D** → orbit with the mouse, set each screen's canvas region (X / Y / W / H) |
| Panic | **BLACKOUT**, or **Ctrl+F12** to pull the output window entirely |

Video with alpha: use **ProRes 4444** or **QuickTime Animation** (`.mov`) for
lower thirds and animated graphics on the GRAPHICS layer.

Files:

- Show: `%APPDATA%\AV.Playout\show.json` (or `AV.Playout.exe --show D:\shows\home-opener.json`)
- As-run (proof of play): `%LOCALAPPDATA%\AV.Playout\as-run\yyyy-MM-dd.csv`
- Logs: `%LOCALAPPDATA%\AV.Playout\logs\`

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```
dotnet run --project src/AV.Playout
```

Single-file exe:

```
dotnet publish src/AV.Playout -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Set the program raster (`CanvasWidth` / `CanvasHeight`) in `App.xaml.cs` to match
your LED processor's input. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.
