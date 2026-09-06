using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PalworldServerManager.Client.Avalonia.Views;

public sealed class AngularCard : Control
{
    public IBrush? Accent { get; set; }
    public override void Render(DrawingContext context)
    {
        base.Render(context); if (Accent is null) return;
        var pen = new Pen(Accent, 2); var width = Bounds.Width - 1; var height = Bounds.Height - 1;
        foreach (var corner in new[] { new Point(1, 1), new Point(width, 1), new Point(1, height), new Point(width, height) })
        {
            context.DrawLine(pen, corner, corner + new Vector(corner.X == 1 ? 12 : -12, 0));
            context.DrawLine(pen, corner, corner + new Vector(0, corner.Y == 1 ? 12 : -12));
        }
    }
}
