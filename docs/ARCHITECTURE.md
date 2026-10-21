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
   decodes through LibVLC's *video callbacks* (`SetVideoFormat` /
   `SetVideoCallbacks`) straight into a double-buffered `WriteableBitmap` that
   the media layer displays. Upgrade path if 4K60 CPU copies get too expensive:
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

## 3. Hardware triggering plan (Phase 3)

- **Raw Input** (`RegisterRawInputDevices` with `RIDEV_INPUTSINK`) receives keys
  while the app is in the background *and* tells us which physical device sent
  them, so the macro keypad can be told apart from the operator's keyboard.
- **Low-level keyboard hook** (`WH_KEYBOARD_LL`) is used alongside it only to
  *swallow* keypad keystrokes so they don't type into whatever has focus. Raw
  Input can identify but not suppress; the hook can suppress but not identify.
- Practical tip: program the keypad to send **F13–F24** (and modifiers). No
  normal application uses them, so collisions disappear.
- **MIDI**: DryWetMIDI (or NAudio.Midi) input device → note/CC → trigger id.
- **Takeovers** (e.g. "Home Goal"): a command that snapshots the current layer
  state, plays the blast clip on the media layer, then restores the snapshot when
  the clip ends or the operator hits the trigger again.

## 4. File structure

✅ = delivered in Phase 1. Everything else is where it will land.

```
AV.Playout/
├─ AV.Playout.slnx
├─ README.md
├─ docs/
│  └─ ARCHITECTURE.md
└─ src/AV.Playout/
   ├─ AV.Playout.csproj                 ✅
   ├─ app.manifest                        ✅ Per-Monitor-V2 DPI awareness
   ├─ App.xaml / App.xaml.cs              ✅ composition root, keep-awake, crash guard
   ├─ Themes/
   │  └─ Dark.xaml                        ✅ control-room theme
   ├─ Core/
   │  ├─ ObservableObject.cs              ✅
   │  ├─ PlayoutEngine.cs                 ✅ layer state + command execution
   │  ├─ Commands/PlayoutCommands.cs      ✅ command records
   │  ├─ Layers/LayerKind.cs              ✅
   │  ├─ Layers/LayerState.cs             ✅
   │  └─ Takeovers/TakeoverController.cs     P3 snapshot / blast / restore
   ├─ Media/
   │  ├─ StillItem.cs                     ✅ pre-decoded GPU-ready stills
   │  ├─ ClipItem.cs                         P2
   │  ├─ MediaBin.cs                         P2 grid bin, preload policy
   │  └─ VlcFrameSource.cs                   P2 LibVLC → WriteableBitmap, seamless loop
   ├─ Live/
   │  ├─ IFrameSource.cs                     P2 shared contract for every video source
   │  ├─ NdiFrameSource.cs                   P4
   │  └─ CaptureFrameSource.cs               P4 (LibVLC dshow:// or Media Foundation)
   ├─ Graphics/
   │  ├─ GameClock.cs                        P5
   │  ├─ PenaltyTimer.cs                     P5
   │  └─ Templates/ScoreBug.xaml             P5
   ├─ Triggers/
   │  ├─ ITriggerSource.cs                   P3
   │  ├─ RawInputKeypadSource.cs             P3
   │  ├─ LowLevelKeyboardHook.cs             P3
   │  ├─ MidiTriggerSource.cs                P3
   │  └─ CueMap.cs  (+ cuemap.json)          P3
   ├─ Services/
   │  ├─ DisplayService.cs                ✅ monitor enumeration (physical px)
   │  └─ ShowFileService.cs                  P6 save/load show
   ├─ Interop/
   │  └─ NativeMethods.cs                 ✅ user32 / kernel32 P/Invoke
   ├─ Controls/
   │  └─ CompositorView.xaml(.cs)         ✅ the layer stack, used by both windows
   └─ Windows/
      ├─ ControlWindow.xaml(.cs)          ✅ operator dashboard
      └─ OutputWindow.xaml(.cs)           ✅ borderless LED output
```

When the Core grows (P3+), split `Core/`, `Triggers/` and the frame-source
contracts into an `AV.Playout.Core` class library so it can be unit-tested
without WPF windows.

## 5. Roadmap

| Phase | Deliverable |
|-------|-------------|
| **1** | Dual window, compositor, command bus, display placement *(this drop)* |
| 2 | Media layer: LibVLC frame-callback renderer, preloaded clips, gapless loops, grid media bin, PVW/PGM |
| 3 | Hardware triggers: Raw Input + LL hook, MIDI, cue map, takeovers |
| 4 | Live layer: NDI receiver, capture card |
| 5 | Graphics layer: game clock, penalty timers, score bug templates |
| 6 | Show files, as-run log, watchdog, config UI |

## 6. Timing notes

WPF composes once per display refresh. A command executed on the UI thread is
on the wall at the next vsync (≤ 16.7 ms at 60 Hz, ≤ 20 ms at 50 Hz). Match the
LED processor's input rate to your content frame rate (50 or 60) to avoid
judder; WPF cannot genlock.
