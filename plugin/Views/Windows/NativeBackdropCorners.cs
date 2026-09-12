using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace QQListener.Views.Windows;

/// <summary>裁切本窗口的 Cocoa 容器，让 Avalonia 内容与原生模糊层使用同一圆角。</summary>
internal static class NativeBackdropCorners
{
    public static bool Apply(Window window, double radius)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0) return false;
        var nativeWindow = handle.HandleDescriptor switch
        {
            "NSWindow" => handle.Handle,
            "NSView" => Get(handle.Handle, Selector("window")),
            _ => 0
        };
        if (nativeWindow == 0) return false;

        // Avalonia 11.3.17 的 AutoFitContentView 同时承载绘制视图和 _blurBehind。
        // 裁切共同父层，不能只裁切 XAML Border 或其中的绘制视图。
        var container = Get(nativeWindow, Selector("contentView"));
        var layer = Get(container, Selector("layer"));
        if (layer == 0) return false;
        SetDouble(layer, Selector("setCornerRadius:"), radius);
        SetBool(layer, Selector("setMasksToBounds:"), 1);

        // 模糊模式默认的 NSWindow 背景仍是方形，清空它，保留容器内的原生模糊。
        var clear = Get(GetClass("NSColor"), Selector("clearColor"));
        SetObject(nativeWindow, Selector("setBackgroundColor:"), clear);
        return Math.Abs(GetDouble(layer, Selector("cornerRadius")) - radius) < 0.01
               && GetBool(layer, Selector("masksToBounds")) != 0;
    }

    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Get(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SetObject(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SetDouble(nint receiver, nint selector, double value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern double GetDouble(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SetBool(nint receiver, nint selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte GetBool(nint receiver, nint selector);
}
