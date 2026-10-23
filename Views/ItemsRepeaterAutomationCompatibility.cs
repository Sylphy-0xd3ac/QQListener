using Avalonia.Automation.Peers;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HarmonyLib;

namespace QQListener.Views;

/// <summary>Compatibility for the empty-list accessibility crash in FluentAvalonia 2.4.1.</summary>
internal static class ItemsRepeaterAutomationCompatibility
{
    private static readonly object Sync = new();
    private static bool _installed;

    internal static void Install()
    {
        lock (Sync)
        {
            if (_installed) return;
            // Avalonia's base peer returns null when there are no visual children. This
            // version of FluentAvalonia reads Count without checking that result. The
            // host's NavigationView also owns empty repeaters, so fix the peer before
            // any settings window is created, rather than changing individual rows.
            var original = AccessTools.DeclaredMethod(typeof(ItemsRepeaterAutomationPeer), "GetChildrenCore");
            new Harmony("QQListener.EmptyRepeaterAutomation")
                .Patch(original, prefix: new HarmonyMethod(typeof(ItemsRepeaterAutomationCompatibility), nameof(HandleEmptyRepeater)));
            _installed = true;
        }
    }

    private static bool HandleEmptyRepeater(
        ItemsRepeaterAutomationPeer __instance, ref IReadOnlyList<AutomationPeer> __result)
    {
        if (__instance.Owner.GetVisualChildren().Any()) return true;
        __result = Array.Empty<AutomationPeer>();
        return false;
    }
}
