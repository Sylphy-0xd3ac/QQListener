using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace QQListener.Views.Windows;

internal static class ThemeResources
{
    internal static void SetResourceReference(this Control control, AvaloniaProperty property, string key) =>
        control.Bind(property, new DynamicResourceExtension(key));
}
