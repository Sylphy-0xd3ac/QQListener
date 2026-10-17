using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using ClassIsland.Core;

namespace QQListener.Views.Windows;

/// <summary>从宿主实际绘制的岛读取位置与材质，也支持宿主的自定义配色和透明度。</summary>
internal static class ClassIslandSurface
{
    // Transform the center itself: host zoom and DPI both affect the visual width.
    internal static PixelPoint BottomCenter(Border background) =>
        background.PointToScreen(new Point(background.Bounds.Width / 2, background.Bounds.Height));

    internal static (Window Window, Border Background)? Find()
    {
        var window = (Application.Current as AppBase)?.MainWindow;
        if (window is null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            window = desktop.Windows.FirstOrDefault(w => w.GetType().FullName == "ClassIsland.MainWindow");
        if (window?.IsVisible != true) return null;
        var background = window?.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Name == "BackgroundBorder" && b.IsEffectivelyVisible
                                 && b.Bounds.Width > 0 && b.Bounds.Height > 0);
        return window is not null && background is not null ? (window, background) : null;
    }
}
