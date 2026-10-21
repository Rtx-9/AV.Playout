using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AV.Playout.Core;
using AV.Playout.Core.Layers;
using AV.Playout.Media;
using AV.Playout.Triggers;
using AV.Playout.Venue;

namespace AV.Playout.Services;

public sealed class ShowFile
{
    public string? OutputDevice { get; set; }
    public bool Rehearsal { get; set; }
    public double Crossfade { get; set; } = 0.5;
    public bool GlobalKeys { get; set; } = true;
    public List<LayerEntry> Layers { get; set; } = [];
    public string HomeName { get; set; } = "HOME";
    public string AwayName { get; set; } = "AWAY";
    public double PeriodMinutes { get; set; } = 20;

    public string LowerThirdTitle { get; set; } = "";
    public string LowerThirdSubtitle { get; set; } = "";
    public string TickerText { get; set; } = "";
    public double TickerSpeed { get; set; } = 140;
    public bool PromptAutoHide { get; set; } = true;
    public double PromptSeconds { get; set; } = 8;

    public List<ScreenEntry> Screens { get; set; } = [];

    public sealed class ScreenEntry
    {
        public ScreenPlacement Placement { get; set; }
        public string Name { get; set; } = "";
        public bool Enabled { get; set; } = true;
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Repeat { get; set; } = 1;
    }

    public sealed class LayerEntry
    {
        public LayerKind Kind { get; set; }
        public double Opacity { get; set; } = 1;
        public List<SlotEntry> Slots { get; set; } = [];
    }

    public sealed class SlotEntry
    {
        public int Column { get; set; }
        public string Name { get; set; } = "";
        public string Source { get; set; } = "";
        public ClipMode Mode { get; set; }
        public string? Hotkey { get; set; }
        public bool InRotation { get; set; }
        public double DwellSeconds { get; set; } = 15;
    }
}

/// <summary>
/// Persists the show (clip grid, hotkeys, routing, team names) to
/// %APPDATA%\AV.Playout\show.json so a restart, or a crash, comes back exactly as it was.
/// </summary>
public static class ShowStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Defaults to %APPDATA%\AV.Playout\show.json; <c>--show &lt;file&gt;</c> on the command line overrides it.</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AV.Playout", "show.json");

    public static ShowFile Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ShowFile>(File.ReadAllText(FilePath), Json) ?? new ShowFile();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt show file must not stop the app from starting.
            System.Diagnostics.Trace.TraceError($"Could not read show file: {ex}");
        }
        return new ShowFile();
    }

    public static void Save(ShowFile show)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(show, Json));
        File.Move(temp, FilePath, overwrite: true);   // atomic: never leave a half-written show
    }

    public static ShowFile Capture(PlayoutEngine engine, ShowFile settings) => new()
    {
        OutputDevice = settings.OutputDevice,
        Rehearsal = settings.Rehearsal,
        GlobalKeys = settings.GlobalKeys,
        Crossfade = engine.CrossfadeSeconds,
        HomeName = engine.Scoreboard.HomeName,
        AwayName = engine.Scoreboard.AwayName,
        PeriodMinutes = engine.Scoreboard.PeriodMinutes,
        LowerThirdTitle = engine.TextGraphics.LowerThirdTitle,
        LowerThirdSubtitle = engine.TextGraphics.LowerThirdSubtitle,
        TickerText = engine.TextGraphics.TickerText,
        TickerSpeed = engine.TextGraphics.TickerSpeed,
        PromptAutoHide = engine.TextGraphics.PromptAutoHide,
        PromptSeconds = engine.TextGraphics.PromptSeconds,
        Screens = engine.Screens.Select(s => new ShowFile.ScreenEntry
        {
            Placement = s.Placement,
            Name = s.Name,
            Enabled = s.Enabled,
            X = s.X,
            Y = s.Y,
            Width = s.Width,
            Height = s.Height,
            Repeat = s.Repeat,
        }).ToList(),
        Layers = engine.LayersBottomUp.Select(layer => new ShowFile.LayerEntry
        {
            Kind = layer.Kind,
            Opacity = layer.Opacity,
            Slots = layer.Slots
                .Where(s => !s.IsEmpty && s.Source is not null)
                .Select(s => new ShowFile.SlotEntry
                {
                    Column = s.Column,
                    Name = s.Name,
                    Source = s.Source!,
                    Mode = s.Mode,
                    Hotkey = s.Hotkey?.Serialize(),
                    InRotation = s.InRotation,
                    DwellSeconds = s.DwellSeconds,
                })
                .ToList(),
        }).ToList(),
    };

    /// <summary>Restores the grid. Thumbnails and stills load in the background.</summary>
    public static async Task ApplyAsync(PlayoutEngine engine, ShowFile show, Action<string> report)
    {
        engine.CrossfadeSeconds = show.Crossfade;
        engine.Scoreboard.HomeName = show.HomeName;
        engine.Scoreboard.AwayName = show.AwayName;
        engine.Scoreboard.PeriodMinutes = show.PeriodMinutes;
        engine.Scoreboard.Apply(Core.Commands.ScoreboardAction.ResetClock);

        var text = engine.TextGraphics;
        text.LowerThirdTitle = show.LowerThirdTitle;
        text.LowerThirdSubtitle = show.LowerThirdSubtitle;
        text.TickerText = show.TickerText;
        text.TickerSpeed = show.TickerSpeed;
        text.PromptAutoHide = show.PromptAutoHide;
        text.PromptSeconds = show.PromptSeconds;

        foreach (var entry in show.Screens)
        {
            if (engine.Screens.FirstOrDefault(s => s.Placement == entry.Placement) is not { } screen)
                continue;
            screen.Name = string.IsNullOrWhiteSpace(entry.Name) ? screen.Name : entry.Name;
            screen.Enabled = entry.Enabled;
            screen.X = entry.X;
            screen.Y = entry.Y;
            screen.Width = entry.Width;
            screen.Height = entry.Height;
            screen.Repeat = entry.Repeat;
        }

        var pending = new List<(ClipSlot Slot, ShowFile.SlotEntry Entry)>();
        foreach (var entry in show.Layers)
        {
            var layer = engine.GetLayer(entry.Kind);
            layer.Opacity = entry.Opacity;
            foreach (var s in entry.Slots.Where(s => s.Column >= 0 && s.Column < layer.Slots.Count))
            {
                var slot = layer.Slots[s.Column];
                slot.Mode = s.Mode;
                slot.Hotkey = Hotkey.Deserialize(s.Hotkey);
                slot.InRotation = s.InRotation;
                slot.DwellSeconds = s.DwellSeconds;
                pending.Add((slot, s));
            }
        }

        // One at a time: thumbnailing many videos in parallel would hammer the disk.
        foreach (var (slot, entry) in pending)
        {
            var kind = ClipLoader.Classify(entry.Source);
            if (kind != ClipKind.Live && !File.Exists(entry.Source))
            {
                // Keep the slot (drive not mounted yet?) so the next save doesn't lose it.
                slot.Name = entry.Name;
                slot.Source = entry.Source;
                slot.Kind = kind;
                report($"Missing media: {entry.Source}");
                continue;
            }
            try
            {
                await ClipLoader.LoadAsync(slot, entry.Source, entry.Name);
            }
            catch (Exception ex)
            {
                report($"Could not load {entry.Name}: {ex.Message}");
            }
        }
    }
}
