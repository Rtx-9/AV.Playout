namespace AV.Playout.Core.Layers;

/// <summary>Compositor layers, bottom to top. The background colour sits beneath all of them.</summary>
public enum LayerKind
{
    Media = 1,
    Live = 2,
    Graphics = 3,
}
