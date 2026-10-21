using System.Windows.Controls;

namespace AV.Playout.Controls;

/// <summary>
/// Renders the program layer stack. Used by the output window and, scaled down,
/// as the program monitor in the control window. Both bind to the same engine.
/// </summary>
public partial class CompositorView : UserControl
{
    public CompositorView()
    {
        InitializeComponent();
    }
}
