using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using AV.Playout.Core;
using AV.Playout.Core.Layers;
using AV.Playout.Media;

namespace AV.Playout.Services;

public sealed record AsRunEntry(
    DateTime Start, DateTime End, LayerKind Layer, int Column, string Clip, string Source, TriggerOrigin Origin)
{
    public double Seconds => Math.Round((End - Start).TotalSeconds, 1);
    public string StartText => Start.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string DurationText => TimeSpan.FromSeconds(Math.Round(Seconds)).ToString(@"m\:ss", CultureInfo.InvariantCulture);
}

/// <summary>Per-clip airtime for the day: what a sponsor report needs.</summary>
public sealed class AsRunTotal(string clip) : ObservableObject
{
    private int _plays;
    private double _seconds;

    public string Clip { get; } = clip;
    public int Plays { get => _plays; set => SetField(ref _plays, value); }

    public double Seconds
    {
        get => _seconds;
        set
        {
            if (SetField(ref _seconds, value))
                OnPropertyChanged(nameof(DurationText));
        }
    }

    public string DurationText => TimeSpan.FromSeconds(Math.Round(_seconds)).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// Proof-of-play ("as-run") log. Every time a clip leaves air, one CSV row is
/// appended to %LOCALAPPDATA%\AV.Playout\as-run\yyyy-MM-dd.csv with its exact start,
/// end and duration, so sponsor airtime can be reported. Rows are written as they
/// complete: a crash loses at most the clips still on air.
/// </summary>
public sealed class AsRunLog
{
    private const string Header = "Date,Start,End,Seconds,Layer,Slot,Clip,Source,Trigger";
    private const int RecentLimit = 200;

    private readonly Dictionary<LayerKind, (ClipSlot Slot, DateTime Start, TriggerOrigin Origin)> _onAir = [];

    public AsRunLog(PlayoutEngine engine)
    {
        foreach (var layer in engine.LayersBottomUp)
            layer.OnAirChanged += (_, change) => OnAirChanged(layer.Kind, change);
        LoadToday();
    }

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AV.Playout", "as-run");

    public static string TodayFile => Path.Combine(Directory, $"{DateTime.Now:yyyy-MM-dd}.csv");

    /// <summary>Newest first.</summary>
    public ObservableCollection<AsRunEntry> Recent { get; } = [];

    /// <summary>Today's airtime per clip, most airtime first.</summary>
    public ObservableCollection<AsRunTotal> Totals { get; } = [];

    private void OnAirChanged(LayerKind layer, OnAirChange change)
    {
        var now = DateTime.Now;
        Close(layer, now);
        if (change.Slot is { } slot)
            _onAir[layer] = (slot, now, change.Origin);
    }

    /// <summary>Closes everything still on air (app shutdown).</summary>
    public void Flush()
    {
        var now = DateTime.Now;
        foreach (var layer in _onAir.Keys.ToList())
            Close(layer, now);
    }

    private void Close(LayerKind layer, DateTime end)
    {
        if (!_onAir.Remove(layer, out var open))
            return;

        var entry = new AsRunEntry(open.Start, end, layer, open.Slot.Column, open.Slot.Name,
            open.Slot.Source ?? "", open.Origin);
        Add(entry);
        Append(entry);
    }

    private void Add(AsRunEntry entry)
    {
        Recent.Insert(0, entry);
        while (Recent.Count > RecentLimit)
            Recent.RemoveAt(Recent.Count - 1);

        var total = Totals.FirstOrDefault(t => t.Clip == entry.Clip);
        if (total is null)
        {
            total = new AsRunTotal(entry.Clip);
            Totals.Add(total);
        }
        total.Plays++;
        total.Seconds += entry.Seconds;

        // Keep the busiest clips on top.
        var index = Totals.IndexOf(total);
        while (index > 0 && Totals[index - 1].Seconds < total.Seconds)
        {
            Totals.Move(index, index - 1);
            index--;
        }
    }

    private static void Append(AsRunEntry e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = TodayFile;
            var row = string.Join(',',
                e.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.Start.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture),
                e.End.ToString("HH:mm:ss.f", CultureInfo.InvariantCulture),
                e.Seconds.ToString("0.0", CultureInfo.InvariantCulture),
                e.Layer,
                e.Column + 1,
                Csv(e.Clip),
                Csv(e.Source),
                e.Origin);
            var text = File.Exists(file) ? row + Environment.NewLine : Header + Environment.NewLine + row + Environment.NewLine;
            File.AppendAllText(file, text, Encoding.UTF8);
        }
        catch (IOException ex)
        {
            Log.Error("Could not write the as-run log", ex);
        }
    }

    /// <summary>Re-reads today's file so totals survive a restart mid-event.</summary>
    private void LoadToday()
    {
        try
        {
            if (!File.Exists(TodayFile))
                return;
            foreach (var line in File.ReadLines(TodayFile).Skip(1))
            {
                var f = ParseCsv(line);
                if (f.Count < 9
                    || !DateTime.TryParse($"{f[0]} {f[1]}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                    || !DateTime.TryParse($"{f[0]} {f[2]}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                    || !Enum.TryParse(f[4], out LayerKind layer)
                    || !int.TryParse(f[5], out var slot)
                    || !Enum.TryParse(f[8], out TriggerOrigin origin))
                    continue;
                Add(new AsRunEntry(start, end, layer, slot - 1, f[6], f[7], origin));
            }
        }
        catch (IOException ex)
        {
            Log.Error("Could not read today's as-run log", ex);
        }
    }

    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    private static List<string> ParseCsv(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else current.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        fields.Add(current.ToString());
        return fields;
    }
}
