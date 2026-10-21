# AV Playout — Architecture

A lightweight live-event playout system for venue LED boards and scoreboards.
C# / WPF on .NET 10, Windows 10/11 x64.

## 1. The big picture

```
 ┌──────────────── TRIGGER SOURCES ───────────────┐
 │ Control UI clicks │ USB macro keypad │ MIDI    │   (later: OSC / HTTP / scoreboard serial)
 └─────────┬──────────────────┬───────────┬───────┘
           │                  │           │
           ▼                  ▼           ▼
     ┌────────────────────────────────────────────┐
     │  CueMap: trigger id ──► PlayoutCommand     │   (JSON, user-editable)
     └────────────────────┬───────────────────────┘
                          ▼
     ┌────────────────────────────────────────────┐
     │  PlayoutEngine  (single source of truth)   │   marshals every command onto the UI thread
     │   Background  │ Media │ Live │ Graphics    │   layer state = plain data + ImageSources
     └────────────────────┬───────────────────────┘
                          │ INotifyPropertyChanged
          ┌───────────────┴────────────────┐
          ▼                                ▼
 ┌──────────────────┐           ┌─────────────────────────┐
 │ CompositorView   │           │ CompositorView           │
 │ in ControlWindow │           │ in OutputWindow          │
 │ (PGM monitor)    │           │ borderless, topmost,     │
 └──────────────────┘           │ pinned to the LED output │
                                └─────────────────────────┘
          ▲                                ▲
          └──────── shared frame buffers ──┘
 ┌───────────────────────────────────────────────────────────┐
 │ FRAME SOURCES (background threads)                        │
 │  VlcFrameSource  (MP4 loops, hype videos)  → WriteableBitmap
 │  NdiFrameSource / CaptureFrameSource       → WriteableBitmap
 └───────────────────────────────────────────────────────────┘
```

### Layer stack (bottom → top)

| Z | Layer      | Content                                   | Source                              |
|---|------------|-------------------------------------------|-------------------------------------|
| 0 | Background | Solid colour                              | `PlayoutEngine.Background`          |
| 1 | Media      | Stills, MP4 sponsor loops, hype videos    | `StillItem` (P1), `VlcFrameSource` (P2) |
| 2 | Live       | Capture card / NDI camera                 | `NdiFrameSource`, `CaptureFrameSource` (P4) |
| 3 | Graphics   | Clocks, penalty timers, score bug (alpha) | Data models + DataTemplates (P5)    |

## 2. Decisions that shape everything else

1. **One state model, rendered twice.** The engine holds layer *data* (brushes,
   `ImageSource`s, view-models). The output and the preview monitor are two
   instances of the same `CompositorView` bound to it. A frozen bitmap or a
   `WriteableBitmap` can be shown in any number of `Image` elements for free, so
   the preview costs almost nothing and can never drift from the output.
   Corollary: never put a `UIElement` in layer state — a visual can only have one
   parent. Graphics are view-models rendered through DataTemplates.

2. **No airspace problems: no HWND hosting inside the compositor.**
   LibVLCSharp's WPF `VideoView` hosts a native child window; WPF cannot draw
   over it with real alpha, which would kill the graphics layer. Instead, video
   decodes through LibVLC's *video callbacks* (`SetVideoFormatCallbacks` /
   `SetVideoCallbacks`) into a ring of native buffers, copied into a
   `WriteableBitmap` once per frame. Upgrade path if 4K60 CPU copies get too expensive:
   D3D11 shared texture → `D3DImage`. NDI frames and capture-card frames use the
   same `WriteableBitmap` contract, so every layer composes with true alpha on
   the GPU.

3. **The output window never uses `AllowsTransparency`.** That flag turns the
   window into a layered window and pushes rendering through a slow software
   path. All transparency happens *inside* the compositor; the window itself is
   opaque black.

4. **Physical-pixel placement on the LED output.** The app is Per-Monitor-V2 DPI
   aware. The output window is pinned with `SetWindowPos` using the monitor's
   physical pixel rectangle, and re-pinned after DPI changes and display
   hot-plugs (LED processors re-enumerate when power-cycled). WPF `Left/Top/
   Width/Height` are in DIPs and are not used for the output.

5. **Everything is a command.** A mouse click, a keypad button and a MIDI note
   all produce the same `PlayoutCommand` and go through
   `PlayoutEngine.Execute`. That gives one place for tally, logging, undo of
   takeovers, and later "as-run" logs for sponsor proof-of-play. `Execute` is
   thread-safe: hook and MIDI callbacks arrive on other threads and are
   marshalled to the UI thread at `DispatcherPriority.Send`.

6. **The wall must stay up.** The output window can't be Alt+F4'd, never takes
   focus (`WS_EX_NOACTIVATE`), is hidden from Alt+Tab (`WS_EX_TOOLWINDOW`), hides
   the cursor, and the app blocks display sleep. Unhandled UI exceptions are
   logged rather than crashing the process.

## 3. Requirements status

How the "Feature Requirements Document" maps onto the code, and where it
deliberately deviates.

| Requirement | Status | Notes |
|---|---|---|
| Auto-detect displays, output on the external one, dashboard on primary | ✅ | `DisplayService` (Win32 `EnumDisplayMonitors`) + `OutputWindow.ShowOn`. **Deviation:** not `Screen.AllScreens` + `WindowState=Maximized`. Maximize works in DIPs and snaps to whichever monitor WPF thinks the window is on, which breaks with mixed DPI scaling. `SetWindowPos` with the monitor's physical rectangle is exact and is re-applied on DPI change and display hot-plug. |
| Strict z-order: Media < Live < Graphics | ✅ | `CompositorView`: background, then `LayersBottomUp`, scorebug last. |
| MP4/MOV hardware-accelerated looping | ✅ | `VlcSource` (LibVLCSharp, `input-repeat`). |
| Live ingest: capture cards | ✅ | LIVE row, `dshow://` (webcams, capture cards, Blackmagic DeckLink via its DirectShow driver), plus `rtsp://`, `srt://`, `udp://` streams. |
| Live ingest: NDI | ⏳ | Needs the NDI runtime and a wrapper (the NDI SDK's C# interop or a NuGet wrapper). Plugs in as another `IFrameSource`. |
| Data overlay with native text | ✅ | `ScoreBug.xaml` bound to `Scoreboard`: clock, periods, scores, penalties. |
| HTML overlay (CefSharp) | ⏳ | Deferred: ~150 MB dependency. Would render off-screen into the GRAPHICS layer as another `IFrameSource`. |
| Global keyboard hook (`SetWindowsHookEx`) | ✅ | `GlobalKeyboardHook` (WH_KEYBOARD_LL). Swallows claimed keys, filters auto-repeat, toggleable. |
| "Home Goal" macro: mute live, fire animation | ✅ | Clip mode **TAKEOVER**: gates every layer above it except graphics, restores the previous clip at the end. |
| DoubleAnimation crossfades | ✅ | `Deck.FadeTo`: each layer has A/B decks; the new deck only fades in once its first frame has decoded, so a trigger never flashes black. |
| Alpha video (.webm / ProRes 4444) | ✅ / ⚠ | BGRA pipeline keeps alpha: ProRes 4444 and QuickTime Animation work (verified with QT Animation). WebM VP9 alpha is not decoded with alpha by LibVLC 3; convert those to ProRes 4444. |
| Pre-buffered preview, async loading | ✅ | Right-click cues into PREVIEW paused on frame one; TAKE hands that same decoder to program. Stills decode off the UI thread; thumbnails load in the background. |
| Decode/ingest off the UI thread | ✅ | LibVLC decodes on its own threads into a native buffer ring; the UI thread only copies the newest frame once per vsync. The game clock is Stopwatch-driven, so a heavy decode can't slow it. |

## 4. Hardware triggering

- **Now:** `WH_KEYBOARD_LL` hook. Program the keypad to send **F13–F24** (and
  modifiers): no normal application uses them, so nothing collides.
- **Next:** Raw Input (`RegisterRawInputDevices` + `RIDEV_INPUTSINK`) to tell
  *which* device sent a key, so a keypad can use ordinary keys without stealing
  them from the operator's keyboard. Raw Input identifies; the hook suppresses.
- **MIDI:** DryWetMIDI (or NAudio.Midi) note/CC → the same `TriggerSlotCommand`.

## 5. File structure

```
AV.Playout/
├─ AV.Playout.slnx
├─ README.md
├─ docs/ARCHITECTURE.md
└─ src/AV.Playout/
   ├─ AV.Playout.csproj            LibVLC payload trimmed to x64 + needed plugins
   ├─ app.manifest                 Per-Monitor-V2 DPI awareness
   ├─ App.xaml(.cs)                composition root, keep-awake, crash guard + log, --show
   ├─ Themes/Dark.xaml             control-room theme (faders, toggles, combo, scrollbars)
   ├─ Core/
   │  ├─ PlayoutEngine.cs          program state, triggers, cue/take, takeovers, rotation, 10 Hz countdown tick
   │  ├─ Commands/PlayoutCommands.cs
   │  └─ Layers/  Layer.cs (clip row + A/B decks + countdown), Deck.cs (animatable), LayerKind.cs
   ├─ Media/
   │  ├─ IFrameSource.cs           contract for every picture source (+ StillSource)
   │  ├─ VlcSource.cs              LibVLC → WriteableBitmap, buffer ring, cue/pause, position
   │  ├─ ClipSlot.cs               one grid cell: source, mode, hotkey, rotation, tally
   │  └─ ClipLoader.cs             classify, decode stills, grab thumbnails
   ├─ Graphics/
   │  ├─ Scoreboard.cs             clock, periods, scores, penalties
   │  └─ TextGraphics.cs           lower third, frame-synced ticker, crowd prompts
   ├─ Venue/
   │  ├─ ScreenDefinition.cs       physical screen ↔ canvas region
   │  └─ ArenaModelBuilder.cs      procedural 3D arena (bowl, rink, cube, ribbon, end board)
   ├─ Triggers/  GlobalKeyboardHook.cs, Hotkey.cs
   ├─ Services/  DisplayService.cs, ShowStore.cs, AsRunLog.cs, Log.cs
   ├─ Interop/NativeMethods.cs
   ├─ Controls/  CompositorView, ScoreBug, Converters
   └─ Windows/   ControlWindow (dashboard), OutputWindow (LED output), VenueWindow (3D preview)
```

### Venue visualizer

`VenueWindow` hosts its own `CompositorView` (the canvas map) and textures every
3D screen with a `VisualBrush` of that view's canvas, cropped to the screen's
region (`Viewbox` in canvas pixels) and applied as an `EmissiveMaterial` so the
LEDs glow in the dark bowl. Whatever is on the output (video, crossfades,
scorebug, ticker) appears on the model in real time. The ribbon repeats its
region N times around the bowl by repeating texture coordinates in the mesh;
WPF can't reliably tile a brush across a surface that long and thin. The
mapping is for visualization and planning: the LED processor still receives the
whole canvas and does the real slicing.

## 6. Roadmap

| Phase | Deliverable | |
|-------|-------------|---|
| 1 | Dual window, compositor, command bus, display placement | ✅ |
| 2 | Media layer: LibVLC renderer, clip grid, crossfades, PVW/PGM | ✅ |
| 3 | Global hotkeys, takeovers | ✅ (Raw Input + MIDI next) |
| 4 | Live layer: capture / streams ✅, NDI ⏳ | partial |
| 5 | Scorebug ✅, lower third / ticker / prompts ✅, HTML overlays ⏳ | partial |
| 6 | Show file ✅, as-run log ✅, sponsor rotation ✅, countdowns ✅, venue 3D ✅, watchdog ⏳, settings UI (canvas size) ⏳ | partial |

## 7. Timing notes

WPF composes once per display refresh. A command executed on the UI thread is
on the wall at the next vsync (≤ 16.7 ms at 60 Hz, ≤ 20 ms at 50 Hz) — for
stills. Video adds LibVLC's open + first-frame time (typically 50–150 ms with the
reduced `file-caching`); cue a clip to PREVIEW and TAKE it for zero-latency
starts. Match the LED processor's input rate to your content (50 or 60) to avoid
judder; WPF cannot genlock.
