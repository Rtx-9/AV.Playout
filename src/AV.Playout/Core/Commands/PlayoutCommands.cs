using System.Windows.Media;
using AV.Playout.Core.Layers;
using AV.Playout.Media;

namespace AV.Playout.Core.Commands;

/// <summary>
/// Every on-air action is a command. UI buttons, keypad keys and MIDI notes all
/// produce these and hand them to <see cref="PlayoutEngine.Execute"/>.
/// </summary>
public abstract record PlayoutCommand;

public sealed record SetBackgroundCommand(Color Color) : PlayoutCommand;

public sealed record ShowStillCommand(StillItem Still) : PlayoutCommand;

public sealed record ClearLayerCommand(LayerKind Layer) : PlayoutCommand;

/// <summary>Panic button: everything off, background to black.</summary>
public sealed record BlackoutCommand : PlayoutCommand;
