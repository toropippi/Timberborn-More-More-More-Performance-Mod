using System;
using System.Reflection;
using System.Threading;

namespace T3MP;

/// <summary>
/// Exposes Timberborn's own input-blocked state to the controller that handles
/// the mod's raw keyboard shortcuts. The game sets this while text fields such
/// as chat have keyboard focus, which is why vanilla speed shortcuts do not fire
/// while the player is typing.
/// </summary>
internal static class VanillaInputBlockerState
{
    private static object? _inputBlocker;
    private static PropertyInfo? _isBlockedProperty;
    private static int _isBlocked;

    public static bool IsBlocked => Volatile.Read(ref _isBlocked) != 0;

    public static void Record(bool isBlocked)
    {
        Volatile.Write(ref _isBlocked, isBlocked ? 1 : 0);
    }

    public static void Refresh(object inputBlocker)
    {
        if (!ReferenceEquals(_inputBlocker, inputBlocker))
        {
            _inputBlocker = inputBlocker;
            _isBlockedProperty = inputBlocker.GetType().GetProperty(
                "IsBlocked",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        try
        {
            Record(_isBlockedProperty?.GetValue(inputBlocker, null) is true);
        }
        catch (Exception)
        {
            // If a future game update changes InputBlocker, preserve the
            // existing hotkey behavior instead of breaking the controller.
            Record(false);
        }
    }
}
