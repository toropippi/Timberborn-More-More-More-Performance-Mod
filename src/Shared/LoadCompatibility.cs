using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace T3MP.Loading;

internal static class LoadCompatibility
{
    // 1.1.2.4 Steam: audited normalized IL, fields, signatures and interfaces
    // against the reviewed 1.1.2.0 build. See docs/load-steam-1124-2026-09-10.md.
    // This admits only these exact module pairs, not future game versions.
    private static readonly bool SteamBaseline = Environment.GetCommandLineArgs().Contains("-t3mpTestSteamLoadBaseline");
    private static readonly Dictionary<string, string> Steam1124 = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Timberborn.BaseComponentSystem|c0f7b920-5694-4612-bdd5-b736144e1f02"] = "84eaa5bf-c386-4f0b-93c7-c33ad9cac169",
        ["Timberborn.BlockObjectModelSystem|a1669a88-0ece-4f14-95d1-bc59fe5e6eaa"] = "9d160114-f6b6-4850-ad1f-61144fba5624",
        ["Timberborn.BlockObstacles|825233ec-0cd7-4d95-b79d-3ab47b8c4333"] = "5459aa5f-0936-487c-a687-3848e16258d5",
        ["Timberborn.BlockSystem|08ba0380-b21b-4e84-999e-011c1cc0b67d"] = "b304e337-d284-45a4-b7f9-326639767e14",
        ["Timberborn.BlockSystemNavigation|096d1a2b-1134-4849-b128-c75707ff8520"] = "bc268f5c-99cd-4fe7-bc5d-bdd28d160ae2",
        ["Timberborn.BlueprintSystem|41363a54-def1-40c7-9ff3-f884cf81cf81"] = "8eaeb125-925f-417a-9232-b82471cdc257",
        ["Timberborn.Buildings|67e23e56-01c7-4200-bfa4-b735eaa5f28a"] = "42f8a5e3-2365-474d-aa9a-9736481946ac",
        ["Timberborn.Common|88d60edf-d568-470b-af44-77abc48c8bd0"] = "89c5de49-7894-4a32-afe4-0819cef431f1",
        ["Timberborn.Coordinates|9efec216-4ae7-43da-9563-c8fc7537bcf1"] = "6bbc7996-1bb7-464c-aa9f-83f57a3fce01",
        ["Timberborn.ForestryEffects|f24d9580-3f86-4f8e-9d28-83f62a3a290a"] = "9de9a5c7-b487-4448-9ab9-26669b0ff255",
        ["Timberborn.GoodStackSystem|253feb51-a590-49c2-ba69-4b6b941c6bc8"] = "6954696a-8030-4f1a-ad3e-fd924341f59e",
        ["Timberborn.InventorySystem|f7d5c441-f4f7-42ed-8497-887e776da98a"] = "54c30882-f3f8-4d52-af9b-ef963c459597",
        ["Timberborn.MapIndexSystem|1880d684-3070-4acd-b0ce-cdc7817c43ad"] = "99a9e585-3603-4edc-b5f7-30b8459cdd7a",
        ["Timberborn.MapStateSystem|5902be72-b557-4f7c-87cc-b71eb2e5aa72"] = "87bcaa30-b934-4911-913f-3b75fa32d8dc",
        ["Timberborn.MechanicalConnectorSystem|d5f0a196-1a1f-4540-83ba-4cc0f1cad0a5"] = "2e3350e4-c248-423c-9962-b4f070f69485",
        ["Timberborn.MechanicalSystem|22e640db-81af-4a3a-94fd-72cb8a063d7f"] = "e578a863-6e5c-4936-9622-6d3b474711a5",
        ["Timberborn.NaturalResourcesLifecycleModelSystem|2b446b61-6908-4ba8-8122-cf46a872471e"] = "6a04e7ae-5e60-4a5f-8ba1-033f6908fddf",
        ["Timberborn.NaturalResourcesModelSystem|4b988987-94dd-4862-b337-5e31b1b44423"] = "f006bb63-282f-441a-b5d7-184e25d056e8",
        ["Timberborn.Navigation|b848ebf8-28b6-4c17-ab4f-f91582085afa"] = "bbe52087-7a1c-4d96-b0d1-4bb2d536d63e",
        ["Timberborn.PrefabOptimization|0c0517bf-185a-46c6-ab97-00a8a5e74af7"] = "b245eaa9-62e5-4aa1-a5ae-4843a79e00c0",
        ["Timberborn.Rendering|03c679ef-24e9-4bac-90dc-b2cd3e78aef9"] = "7678cf05-d2a4-4ac3-bfdc-18af6d2ca6be",
        ["Timberborn.SelectionSystem|306a9bc7-0918-47a0-96f0-1efe1a97d3a2"] = "2b5c6a90-9b3f-4af7-9cdf-6ea423bc406d",
        ["Timberborn.SingletonSystem|962512a9-30fb-4e42-b29f-b0115c9e9015"] = "1ea34549-37d4-474f-8175-54bf096ffc88",
        ["Timberborn.SlotSystem|b38ee034-1457-4300-a8ea-68cb033266f9"] = "1d736ff0-f980-46cc-bf20-272e67345dd4",
        ["Timberborn.TerrainSystem|271afb68-a1c7-4cdf-8752-b795e0a3c178"] = "9d6ad024-2d79-47e2-b50f-c22cf46b5ce4",
        ["Timberborn.TerrainSystemRendering|db00dee5-7277-4b2d-aec5-67cf8e8703e4"] = "15bc5023-1fee-4482-b149-e600add6602e",
        ["Timberborn.TextureOperations|90b26ce4-8697-429b-9b5f-1e15ab552e8d"] = "e84d244a-fa65-4a5b-8e9e-f358a20a7859",
        ["Timberborn.TubeSystem|51b4e043-c4f9-47d9-92da-7a9e70baa2d1"] = "43f3a5ac-42bb-478a-854b-fd104bc8b228",
        ["Timberborn.WaterBuildings|8efae8cc-faad-45bf-8be5-2f90d9e40435"] = "720a9f1e-dc30-4d77-86c7-d4456fedc32c",
        ["Timberborn.WorldPersistence|322e064a-da3b-4021-9703-113b13123a6f"] = "fc330c06-b875-4926-8f4e-cd2e5939a655",
    };
    internal static bool ReviewedModule(string name, string expected, string? actual) =>
        actual == expected || !SteamBaseline && Steam1124.TryGetValue(name + "|" + expected, out var steam) && actual == steam;

    internal static bool Reviewed(params string[] modules)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        return modules.All(row =>
        {
            var parts = row.Split('|');
            var actual = assemblies.FirstOrDefault(a => a.GetName().Name == parts[0])?.ManifestModule.ModuleVersionId.ToString();
            return ReviewedModule(parts[0], parts[1], actual);
        });
    }

    internal static bool Unmodified(IEnumerable<MethodBase> methods, string owner, Func<MethodBase, string, bool>? reviewed = null)
    {
        var getInfo = LoadPatchBridge.Find("HarmonyLib.Harmony").GetMethod("GetPatchInfo", LoadPatchBridge.All)!;
        foreach (var method in methods)
        {
            var info = getInfo.Invoke(null, new object[] { method });
            if (info == null) continue;
            var owners = (IEnumerable<string>)info.GetType().GetProperty("Owners", LoadPatchBridge.All)!.GetValue(info)!;
            var foreign = owners.FirstOrDefault(o => o != owner && !(reviewed?.Invoke(method, o) ?? false));
            if (foreign != null)
            {
                UnityEngine.Debug.Log("[T3MPLOADCOMPAT] fallback " + method.DeclaringType?.FullName + "." + method.Name + " owner=" + foreign);
                return false;
            }
        }
        return true;
    }

    internal static bool MainInstalled(Type localType)
    {
        var main = AppDomain.CurrentDomain.GetAssemblies().Where(a => a != localType.Assembly)
            .Select(a => a.GetType(localType.FullName!)).FirstOrDefault(t => t != null);
        return main != null && (bool)(main.GetProperty("Installed", LoadPatchBridge.All)?.GetValue(null) ?? false);
    }
}
