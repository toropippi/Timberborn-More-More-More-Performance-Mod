using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Timberborn.Common;
using Timberborn.Goods;
using Timberborn.InventorySystem;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Inventory.GetCapacity and InventoryFillCalculator.GetInventoryFillPercentage
// walk an inventory's allowed-good rows and, for every row, call
// Inventory.LimitedAmount, which searches the same row list again by string
// for the row's own amount (StorableGoodRegistry.GetAmount, first match).
// Every inventory initializer lists each good once (vanilla data and
// GetAllowedGoods' Distinct), so the first match is the row in hand and the
// amount is taken from the row. The disallower query, the Math.Min, the row
// order, every other call and all outputs are unchanged.
internal static class AllowedGoodRows
{
    private const string Owner = "t3mp.runtime.allowed-good-rows";
    // Game 1.1.2.7 added Inventory._specIgnorableCapacity, checked by native
    // GetCapacity right after _ignorableCapacity; older builds lack the field.
    private static readonly Func<Inventory, bool>? SpecIgnorableCapacity = FieldReader("_specIgnorableCapacity");
    internal static long RowReads;
    internal static bool Installed { get; private set; }

    internal static void Install(Type harmonyType, Type harmonyMethodType, MethodInfo patch)
    {
        if (Installed) return;
        RuntimePatches.Apply(Owner, harmonyType, harmonyMethodType, patch, apply =>
        {
            apply(RuntimePatches.Find(typeof(Inventory), "GetCapacity", new[] { typeof(List<GoodAmount>) }), null, null, nameof(RewriteCapacity), null);
            apply(RuntimePatches.Find(typeof(InventoryFillCalculator), "GetInventoryFillPercentage", new[] { typeof(Inventory), typeof(ReadOnlyHashSet<string>), typeof(bool) }),
                null, null, nameof(RewriteFill), null);
        }, typeof(AllowedGoodRows));
        Installed = true;
        Debug.Log("[T3MP] Allowed-good row amounts installed.");
    }

    private static Func<Inventory, bool>? FieldReader(string name)
    {
        var field = typeof(Inventory).GetField(name, RuntimePatches.All);
        if (field == null) return null;
        var method = new DynamicMethod("T3MP." + name, typeof(bool), new[] { typeof(Inventory) }, typeof(AllowedGoodRows).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (Func<Inventory, bool>)method.CreateDelegate(typeof(Func<Inventory, bool>));
    }

    // Inventory.GetCapacity with the row amount in place of the searched one.
    private static void GetCapacity(Inventory self, List<GoodAmount> capacity)
    {
        if (self._ignorableCapacity) return;
        if (SpecIgnorableCapacity != null && SpecIgnorableCapacity(self)) return;
        foreach (StorableGoodAmount allowedGood in self.AllowedGoods)
        {
            string goodId = allowedGood.StorableGood.GoodId;
            // Native order: the disallower first, then the row amount.
            RowReads++;
            int num = Math.Min(self._goodDisallower.AllowedAmount(goodId), allowedGood.Amount);
            if (num > 0) capacity.Add(new GoodAmount(goodId, num));
        }
    }

    // InventoryFillCalculator.GetInventoryFillPercentage, same substitution.
    private static (float average, float lowest) GetInventoryFillPercentage(Inventory inventory, ReadOnlyHashSet<string> goods, bool onlyInStock)
    {
        float num = 1f;
        int num2 = 0;
        int num3 = 0;
        foreach (StorableGoodAmount allowedGood in inventory.AllowedGoods)
        {
            string goodId = allowedGood.StorableGood.GoodId;
            if (!goods.Contains(goodId)) continue;
            RowReads++;
            int num4 = Math.Min(inventory._goodDisallower.AllowedAmount(goodId), allowedGood.Amount);
            if (num4 <= 0) continue;
            int num5 = inventory.AmountInStock(goodId);
            if (!onlyInStock || num5 > 0)
            {
                num2 += num4;
                num3 += num5;
                float num6 = (float)num5 / (float)num4;
                if (num6 < num) num = num6;
            }
        }
        num2 = Math.Min(num2, inventory.Capacity);
        return (average: (num2 == 0) ? 0f : Mathf.Clamp01((float)num3 / (float)num2), lowest: num);
    }

    private static IEnumerable<T> RewriteCapacity<T>(IEnumerable<T> instructions) =>
        RuntimePatches.CallAndReturn<T>(typeof(AllowedGoodRows).GetMethod(nameof(GetCapacity), RuntimePatches.All)!, 2);
    private static IEnumerable<T> RewriteFill<T>(IEnumerable<T> instructions) =>
        RuntimePatches.CallAndReturn<T>(typeof(AllowedGoodRows).GetMethod(nameof(GetInventoryFillPercentage), RuntimePatches.All)!, 3);
}
