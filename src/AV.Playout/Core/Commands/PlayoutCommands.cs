using System.Windows.Media;
using AV.Playout.Core.Layers;

namespace AV.Playout.Core.Commands;

/// <summary>
/// Every on-air action is a command. UI clicks, keypad keys and (later) MIDI notes
/// all produce these and hand them to <see cref="PlayoutEngine.Execute"/>.
/// </summary>
public abstract record PlayoutCommand;

/// <summary>Trigger the clip in a slot. An empty slot clears the layer.</summary>
public sealed record TriggerSlotCommand(LayerKind Layer, int Column) : PlayoutCommand;

/// <summary>Trigger every filled slot in a column, across all layers.</summary>
public sealed record TriggerColumnCommand(int Column) : PlayoutCommand;

/// <summary>Pre-buffer a slot in the preview monitor, paused on its first frame.</summary>
public sealed record CueSlotCommand(LayerKind Layer, int Column) : PlayoutCommand;

/// <summary>Take the cued clip to air.</summary>
public sealed record TakeCommand : PlayoutCommand;

public sealed record ClearLayerCommand(LayerKind Layer) : PlayoutCommand;

public sealed record ToggleBypassCommand(LayerKind Layer) : PlayoutCommand;

public sealed record SetBackgroundCommand(Color Color) : PlayoutCommand;

/// <summary>Panic button: every layer out, scorebug off, background black.</summary>
public sealed record BlackoutCommand : PlayoutCommand;

public enum ScoreboardAction
{
    ToggleClock, ResetClock,
    HomeGoal, HomeMinus, AwayGoal, AwayMinus,
    NextPeriod, PreviousPeriod,
    HomePenalty, AwayPenalty,
    ToggleVisible,
}

public sealed record ScoreboardCommand(ScoreboardAction Action) : PlayoutCommand;
