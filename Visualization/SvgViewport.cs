using Lattice.Environment;

namespace Lattice.Visualization;

/// <summary>
/// The size of the SVG canvas a map needs under <see cref="SvgRenderer"/>'s
/// fixed scale and padding. Extracted so frame and trajectory exporters agree
/// on the same viewport without re-deriving geometry from two code paths.
/// </summary>
public sealed record SvgViewport(double Width, double Height)
{
    /// <summary>The SVG viewBox attribute value, e.g. "0 0 120 120".</summary>
    public string ToViewBox() => $"0 0 {SvgRenderer.Num(Width)} {SvgRenderer.Num(Height)}";
}