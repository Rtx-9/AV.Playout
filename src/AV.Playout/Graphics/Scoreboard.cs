using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using AV.Playout.Core;
using AV.Playout.Core.Commands;

namespace AV.Playout.Graphics;

/// <summary>A running penalty. Its clock only runs while the game clock runs.</summary>
public sealed class Penalty : ObservableObject
{
    private TimeSpan _remaining;

    public Penalty(TimeSpan length) => _remaining = length;

    public TimeSpan Remaining
    {
        get => _remaining;
        set
        {
            if (SetField(ref _remaining, value))
                OnPropertyChanged(nameof(Text));
        }
    }

    public string Text => Scoreboard.FormatMinutes(_remaining);

    // Remaining when the game clock last (re)started, and the stopwatch reading then.
    internal TimeSpan BaseRemaining;
    internal TimeSpan BaseElapsed;
}

/// <summary>
/// Hockey-style match data for the graphics layer. The clock is driven by a
/// Stopwatch (no drift from timer jitter) and repainted by a UI-thread timer, so it
/// ticks smoothly no matter how heavy the video decode on the other layers is.
/// </summary>
public sealed class Scoreboard : ObservableObject
{
    private static readonly TimeSpan PenaltyLength = TimeSpan.FromMinutes(2);

    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _timer;
    private TimeSpan _baseRemaining;

    private string _homeName = "HOME", _awayName = "AWAY";
    private int _homeScore, _awayScore, _period = 1;
    private TimeSpan _periodLength = TimeSpan.FromMinutes(20);
    private TimeSpan _remaining = TimeSpan.FromMinutes(20);
    private bool _isRunning, _isVisible;

    public Scoreboard()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
    }

    public string HomeName { get => _homeName; set => SetField(ref _homeName, value); }
    public string AwayName { get => _awayName; set => SetField(ref _awayName, value); }
    public int HomeScore { get => _homeScore; set => SetField(ref _homeScore, Math.Max(0, value)); }
    public int AwayScore { get => _awayScore; set => SetField(ref _awayScore, Math.Max(0, value)); }

    public int Period
    {
        get => _period;
        set
        {
            if (SetField(ref _period, Math.Clamp(value, 1, 9)))
                OnPropertyChanged(nameof(PeriodText));
        }
    }

    public string PeriodText => _period switch { 1 => "1st", 2 => "2nd", 3 => "3rd", 4 => "OT", _ => $"OT{_period - 3}" };

    public double PeriodMinutes
    {
        get => _periodLength.TotalMinutes;
        set => _periodLength = TimeSpan.FromMinutes(Math.Clamp(value, 1, 60));
    }

    public TimeSpan Remaining
    {
        get => _remaining;
        private set
        {
            if (SetField(ref _remaining, value))
                OnPropertyChanged(nameof(ClockText));
        }
    }

    /// <summary>mm:ss, switching to ss.t in the last minute (as arena clocks do).</summary>
    public string ClockText => _remaining >= TimeSpan.FromMinutes(1)
        ? FormatMinutes(_remaining)
        : (Math.Floor(_remaining.TotalSeconds * 10) / 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    public bool IsRunning { get => _isRunning; private set => SetField(ref _isRunning, value); }
    public bool IsVisible { get => _isVisible; set => SetField(ref _isVisible, value); }

    public ObservableCollection<Penalty> HomePenalties { get; } = [];
    public ObservableCollection<Penalty> AwayPenalties { get; } = [];

    public void Apply(ScoreboardAction action)
    {
        switch (action)
        {
            case ScoreboardAction.ToggleClock: if (IsRunning) StopClock(); else StartClock(); break;
            case ScoreboardAction.ResetClock: StopClock(); Remaining = _periodLength; break;
            case ScoreboardAction.HomeGoal: HomeScore++; break;
            case ScoreboardAction.HomeMinus: HomeScore--; break;
            case ScoreboardAction.AwayGoal: AwayScore++; break;
            case ScoreboardAction.AwayMinus: AwayScore--; break;
            case ScoreboardAction.NextPeriod: StopClock(); Period++; Remaining = _periodLength; break;
            case ScoreboardAction.PreviousPeriod: StopClock(); Period--; Remaining = _periodLength; break;
            case ScoreboardAction.HomePenalty: AddPenalty(HomePenalties); break;
            case ScoreboardAction.AwayPenalty: AddPenalty(AwayPenalties); break;
            case ScoreboardAction.ToggleVisible: IsVisible = !IsVisible; break;
        }
    }

    public void RemovePenalty(Penalty penalty)
    {
        HomePenalties.Remove(penalty);
        AwayPenalties.Remove(penalty);
    }

    private void StartClock()
    {
        if (IsRunning || _remaining <= TimeSpan.Zero) return;
        _baseRemaining = _remaining;
        foreach (var p in HomePenalties.Concat(AwayPenalties))
        {
            p.BaseRemaining = p.Remaining;
            p.BaseElapsed = TimeSpan.Zero;
        }
        _stopwatch.Restart();
        _timer.Start();
        IsRunning = true;
    }

    private void StopClock()
    {
        if (!IsRunning) return;
        Tick();
        _stopwatch.Stop();
        _timer.Stop();
        IsRunning = false;
    }

    private void AddPenalty(ObservableCollection<Penalty> list)
    {
        var penalty = new Penalty(PenaltyLength)
        {
            BaseRemaining = PenaltyLength,
            BaseElapsed = _stopwatch.Elapsed,
        };
        list.Add(penalty);
    }

    private void Tick()
    {
        var elapsed = _stopwatch.Elapsed;
        var remaining = _baseRemaining - elapsed;
        Remaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

        foreach (var list in new[] { HomePenalties, AwayPenalties })
        {
            for (var i = list.Count - 1; i >= 0; i--)
            {
                var p = list[i];
                var left = p.BaseRemaining - (elapsed - p.BaseElapsed);
                if (left <= TimeSpan.Zero)
                    list.RemoveAt(i);
                else
                    p.Remaining = left;
            }
        }

        if (_remaining == TimeSpan.Zero)
        {
            _stopwatch.Stop();
            _timer.Stop();
            IsRunning = false;
        }
    }

    internal static string FormatMinutes(TimeSpan t)
    {
        var seconds = (int)Math.Ceiling(t.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }
}
