using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace T3MP;

/// <summary>
/// Renders the tick-rate readout as a UI Toolkit label attached to one of the
/// game's live UIDocuments, so it uses the game's font/theme and sits in the
/// same layer as the HUD (not a floating IMGUI box). The game HUD is UI Toolkit
/// (UIDocument, not a Canvas), so it is not affected by the render blackout and
/// the label stays visible during fast-forward.
///
/// Returns false from TrySetText when no usable document is available; the
/// controller then falls back to the IMGUI overlay.
/// </summary>
internal static class SpeedupUiOverlay
{
    private static Label? _label;
    private static VisualElement? _host;
    private static bool _permanentlyFailed;
    private static int _warnCount;

    public static bool TrySetText(string text)
    {
        if (_permanentlyFailed)
        {
            return false;
        }

        try
        {
            if (!EnsureLabel())
            {
                return false;
            }

            if (_label!.text != text)
            {
                _label.text = text;
            }

            _label.style.display = DisplayStyle.Flex;
            return true;
        }
        catch (Exception exception)
        {
            _permanentlyFailed = true;
            if (_warnCount++ < 3)
            {
                Debug.LogWarning($"[T3MP] Speedup UI overlay disabled: {exception.GetType().Name}: {exception.Message}");
            }

            return false;
        }
    }

    public static void Hide()
    {
        if (_label is not null)
        {
            _label.style.display = DisplayStyle.None;
        }
    }

    private static bool EnsureLabel()
    {
        // Re-attach if the host document was torn down (scene reload etc.).
        if (_label is not null && _host is not null && _label.parent == _host && _host.panel is not null)
        {
            return true;
        }

        var root = FindHudRoot();
        if (root is null)
        {
            return false;
        }

        _label = new Label
        {
            name = "T3MP.SpeedupMeter",
            pickingMode = PickingMode.Ignore
        };

        // Inherit the game font from the parent (do not set unityFont). Style it
        // as a compact HUD readout anchored bottom-right.
        var style = _label.style;
        style.position = Position.Absolute;
        style.right = 12f;
        style.bottom = 12f;
        style.color = new StyleColor(new Color(1f, 1f, 1f, 0.92f));
        style.fontSize = 14f;
        style.unityTextAlign = TextAnchor.MiddleCenter;
        style.paddingLeft = 8f;
        style.paddingRight = 8f;
        style.paddingTop = 3f;
        style.paddingBottom = 3f;
        style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.55f));
        style.borderTopLeftRadius = 4f;
        style.borderTopRightRadius = 4f;
        style.borderBottomLeftRadius = 4f;
        style.borderBottomRightRadius = 4f;

        root.Add(_label);
        _host = root;
        return true;
    }

    private static VisualElement? FindHudRoot()
    {
        // Pick a live, on-screen UIDocument with the largest UI tree — that is
        // the in-game HUD panel stack. Avoids empty/util documents.
        UIDocument? best = null;
        var bestCount = -1;
        foreach (var document in Resources.FindObjectsOfTypeAll<UIDocument>())
        {
            if (document == null || !document.isActiveAndEnabled)
            {
                continue;
            }

            var root = document.rootVisualElement;
            if (root is null || root.panel is null)
            {
                continue;
            }

            var count = root.hierarchy.childCount;
            if (count > bestCount)
            {
                best = document;
                bestCount = count;
            }
        }

        return best?.rootVisualElement;
    }
}
