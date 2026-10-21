using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AV.Playout.Core;

namespace AV.Playout.Graphics;

/// <summary>
/// Live text on the graphics layer: a lower third, a scrolling ticker and big crowd
/// prompts. The ticker position is computed here, once per frame, so the output and
/// the program monitor scroll in exact sync.
/// </summary>
public sealed class TextGraphics : ObservableObject
{
    public const double TickerFontSize = 40;
    private const string Separator = "     •     ";
    private static readonly Typeface TickerTypeface =
        new(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private readonly double _canvasWidth;
    private readonly Stopwatch _tickerClock = new();
    private readonly DispatcherTimer _promptTimer;

    private string _lowerThirdTitle = "";
    private string _lowerThirdSubtitle = "";
    private bool _lowerThirdVisible;

    private string _tickerText = "";
    private bool _tickerVisible;
    private double _tickerSpeed = 140;
    private double _tickerOffset;
    private double _tickerCycle = 1;
    private IReadOnlyList<string> _tickerCopies = [];

    private string _promptText = "MAKE SOME NOISE";
    private bool _promptVisible;
    private bool _promptAutoHide = true;
    private double _promptSeconds = 8;

    public TextGraphics(double canvasWidth)
    {
        _canvasWidth = canvasWidth;
        _promptTimer = new DispatcherTimer();
        _promptTimer.Tick += (_, _) => HidePrompt();
    }

    // ---- Lower third ------------------------------------------------------------------

    public string LowerThirdTitle { get => _lowerThirdTitle; set => SetField(ref _lowerThirdTitle, value); }
    public string LowerThirdSubtitle { get => _lowerThirdSubtitle; set => SetField(ref _lowerThirdSubtitle, value); }
    public bool LowerThirdVisible { get => _lowerThirdVisible; private set => SetField(ref _lowerThirdVisible, value); }

    public void ShowLowerThird() => LowerThirdVisible = !string.IsNullOrWhiteSpace(_lowerThirdTitle);
    public void HideLowerThird() => LowerThirdVisible = false;

    // ---- Ticker -----------------------------------------------------------------------

    public string TickerText
    {
        get => _tickerText;
        set
        {
            if (SetField(ref _tickerText, value))
                RebuildTicker();
        }
    }

    /// <summary>Pixels per second on the canvas.</summary>
    public double TickerSpeed { get => _tickerSpeed; set => SetField(ref _tickerSpeed, Math.Clamp(value, 20, 600)); }

    public bool TickerVisible { get => _tickerVisible; private set => SetField(ref _tickerVisible, value); }

    /// <summary>Canvas.Left of the first text copy (≤ 0).</summary>
    public double TickerOffset { get => _tickerOffset; private set => SetField(ref _tickerOffset, value); }

    /// <summary>The text repeated enough times to cover the canvas width plus one cycle.</summary>
    public IReadOnlyList<string> TickerCopies { get => _tickerCopies; private set => SetField(ref _tickerCopies, value); }

    public void ShowTicker()
    {
        if (string.IsNullOrWhiteSpace(_tickerText) || TickerVisible) return;
        _tickerClock.Restart();
        TickerOffset = 0;
        CompositionTarget.Rendering += OnRendering;
        TickerVisible = true;
    }

    public void HideTicker()
    {
        if (!TickerVisible) return;
        CompositionTarget.Rendering -= OnRendering;
        _tickerClock.Stop();
        TickerVisible = false;
    }

    private void RebuildTicker()
    {
        if (string.IsNullOrWhiteSpace(_tickerText))
        {
            TickerCopies = [];
            HideTicker();
            return;
        }

        var unit = _tickerText.Trim() + Separator;
        var formatted = new FormattedText(unit, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            TickerTypeface, TickerFontSize, Brushes.White, 1.0);
        _tickerCycle = Math.Max(1, formatted.WidthIncludingTrailingWhitespace);
        var copies = (int)Math.Ceiling(_canvasWidth / _tickerCycle) + 1;
        TickerCopies = Enumerable.Repeat(unit, copies).ToList();
    }

    private void OnRendering(object? sender, EventArgs e) =>
        TickerOffset = -(_tickerClock.Elapsed.TotalSeconds * _tickerSpeed % _tickerCycle);

    // ---- Crowd prompts ----------------------------------------------------------------

    public ObservableCollection<string> PromptPresets { get; } =
        ["MAKE SOME NOISE", "DEFENSE!", "LET'S GO!", "GET LOUD!", "CLAP YOUR HANDS", "GOAL!"];

    public string PromptText { get => _promptText; set => SetField(ref _promptText, value); }
    public bool PromptVisible { get => _promptVisible; private set => SetField(ref _promptVisible, value); }
    public bool PromptAutoHide { get => _promptAutoHide; set => SetField(ref _promptAutoHide, value); }
    public double PromptSeconds { get => _promptSeconds; set => SetField(ref _promptSeconds, Math.Clamp(Math.Round(value), 1, 120)); }

    public void ShowPrompt(string? text = null)
    {
        if (text is not null) PromptText = text;
        if (string.IsNullOrWhiteSpace(_promptText)) return;

        // Re-showing restarts the pulse animation in the views.
        PromptVisible = false;
        PromptVisible = true;

        _promptTimer.Stop();
        if (_promptAutoHide)
        {
            _promptTimer.Interval = TimeSpan.FromSeconds(_promptSeconds);
            _promptTimer.Start();
        }
    }

    public void HidePrompt()
    {
        _promptTimer.Stop();
        PromptVisible = false;
    }

    public void HideAll()
    {
        HideLowerThird();
        HideTicker();
        HidePrompt();
    }
}
