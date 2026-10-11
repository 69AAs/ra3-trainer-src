namespace Ra3Trainer.Core.Runtime;

/// <summary>
/// Maps an in-game left-button press onto the offscreen panel's coordinate space.
/// The panel bitmap is rendered at 96 DPI, so one game pixel is exactly one panel
/// unit and no scaling maths is involved. A press outside the panel is not ours.
/// </summary>
public static class PanelClickMap
{
    public static bool TryMap(long gameX, long gameY, int panelX, int panelY, int width, int height,
                              out int x, out int y)
    {
        x = y = 0;
        if (width <= 0 || height <= 0) return false;
        // The wire format is a 32-bit word per axis, so anything outside that range is
        // not a real press. Checking first also keeps the subtraction below overflow-free.
        if (gameX is < int.MinValue or > int.MaxValue || gameY is < int.MinValue or > int.MaxValue) return false;
        var localX = gameX - panelX;
        var localY = gameY - panelY;
        if (localX < 0 || localY < 0 || localX >= width || localY >= height) return false;
        x = (int)localX;
        y = (int)localY;
        return true;
    }
}
