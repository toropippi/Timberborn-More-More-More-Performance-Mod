using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Timberborn.WaterObjects;

namespace T3MP;

// 不変座標の解決結果だけをwater objectごとに保持する。
// Stores only immutable-coordinate resolution for each water object.
internal sealed class WaterObjectFastPathEntry
{
    internal readonly WaterObject WaterObject;
    internal bool Resolved;
    internal bool OnMap;
    internal int Cell2D;
    internal int BaseZ;

    internal WaterObjectFastPathEntry(WaterObject waterObject)
    {
        WaterObject = waterObject;
    }
}

internal sealed class WaterObjectReferenceEqualityComparer :
    IEqualityComparer<WaterObject>
{
    internal static readonly WaterObjectReferenceEqualityComparer Instance =
        new WaterObjectReferenceEqualityComparer();

    public bool Equals(WaterObject x, WaterObject y)
    {
        return ReferenceEquals(x, y);
    }

    public int GetHashCode(WaterObject obj)
    {
        return RuntimeHelpers.GetHashCode(obj);
    }
}
