# 製品から撤去した実験フック（2026-09-16）

[分離規約](../docs/DIAGNOSTICS.md)により、製品ソースの`#if *_EXPERIMENT`ブロック28箇所と`T3MP.csproj`の実験用プロパティ
（`HarvestTreeExperiment`、`TickPortExperiment`、`TickDispatchExperiment`、`HarvestIndexExperiment`）を撤去した。
これらは未コミットの作業ツリーにしかなかったため、再試作の参考として撤去内容をここに残す。
再試作するときは、フックを製品に戻すのではなく、候補DLLを別ブランチで作り`scripts/measure.ps1 -Kind ab -Candidate`で測る。

## T3MP.csproj

```xml
<PropertyGroup Condition="'$(HarvestTreeExperiment)' == 'true'"><DefineConstants>$(DefineConstants);HARVEST_TREE_EXPERIMENT</DefineConstants></PropertyGroup>
<PropertyGroup Condition="'$(TickPortExperiment)' == 'true'"><DefineConstants>$(DefineConstants);TICK_PORT_EXPERIMENT</DefineConstants></PropertyGroup>
<ItemGroup Condition="'$(TickPortExperiment)' == 'true'"><Compile Include="../../experiments/tick-port/*.cs" Link="Runtime/%(Filename)%(Extension)" /></ItemGroup>
<PropertyGroup Condition="'$(TickDispatchExperiment)' == 'true'"><DefineConstants>$(DefineConstants);TICK_DISPATCH_EXPERIMENT</DefineConstants></PropertyGroup>
<ItemGroup Condition="'$(TickDispatchExperiment)' == 'true'"><Compile Include="../../experiments/tick-dispatch/TickDispatch.cs" Link="Runtime/TickDispatch.cs" /></ItemGroup>
<PropertyGroup Condition="'$(HarvestIndexExperiment)' == 'true'"><DefineConstants>$(DefineConstants);HARVEST_INDEX_EXPERIMENT</DefineConstants></PropertyGroup>
<ItemGroup Condition="'$(HarvestIndexExperiment)' == 'true'">
  <Compile Include="../../experiments/harvest-index/*.cs" Link="Runtime/%(Filename)%(Extension)" />
  <Publicize Include="Timberborn.YielderFinding" IncludeCompilerGeneratedMembers="false" />
  <Reference Include="UnityEngine.JSONSerializeModule"><HintPath>$(TimberbornManagedDir)\UnityEngine.JSONSerializeModule.dll</HintPath><Private>false</Private></Reference>
</ItemGroup>
<ItemGroup Condition="'$(HarvestTreeExperiment)' == 'true'">
  <Compile Include="../../experiments/harvest-tree/*.cs" Link="Runtime/%(Filename)%(Extension)" />
  <Publicize Include="Timberborn.YielderFinding" IncludeCompilerGeneratedMembers="false" />
  <Reference Include="UnityEngine.JSONSerializeModule"><HintPath>$(TimberbornManagedDir)\UnityEngine.JSONSerializeModule.dll</HintPath><Private>false</Private></Reference>
</ItemGroup>
<Reference Include="Timberborn.Metrics" Condition="'$(TickPortExperiment)' == 'true'"><HintPath>$(TimberbornManagedDir)\Timberborn.Metrics.dll</HintPath><Private>false</Private></Reference>
```

## src/T3MP/ModSettings.cs

```csharp
#if TICK_PORT_EXPERIMENT
    public static readonly bool EnableTickComponentDispatch = !HasCommandLineFlag("-t3mpTestNoComponentCalls");
#endif
#if TICK_DISPATCH_EXPERIMENT
    public static readonly bool EnableTickDispatch = !HasCommandLineFlag("-t3mpTestNoTickDispatch");
#endif
```

## src/T3MP/Runtime/RuntimePatches.cs（Install の TickFrontier 後 / TerrainNeighborVisits 後）

```csharp
#if TICK_PORT_EXPERIMENT
            if (TickFrontier.Installed && ModSettings.EnableTickComponentDispatch) TickComponentDispatch.Install(harmonyType, harmonyMethodType, patch);
#endif
#if TICK_DISPATCH_EXPERIMENT
            if (ModSettings.EnableTickDispatch) TickDispatch.Install(harmonyType, harmonyMethodType, patch);
#endif
            ...
#if HARVEST_TREE_EXPERIMENT
            HarvestCandidateTree.Install(harmonyType,harmonyMethodType,patch);
#endif
#if HARVEST_INDEX_EXPERIMENT
            HarvestIndex.Install(harmonyType,harmonyMethodType,patch);
#endif
```

## src/T3MP/Loading/LoadPatches.cs（Revalidate 列、TickFrontier 後 / TerrainNeighborVisits 後）

```csharp
#if TICK_PORT_EXPERIMENT
        Runtime.TickComponentDispatch.Revalidate();
#endif
#if TICK_DISPATCH_EXPERIMENT
        Runtime.TickDispatch.Revalidate();
#endif
        ...
#if HARVEST_TREE_EXPERIMENT
        Runtime.HarvestCandidateTree.Revalidate();
#endif
#if HARVEST_INDEX_EXPERIMENT
        Runtime.HarvestIndex.Revalidate();
#endif
```

## src/Shared/LoadPatchBridge.cs（Create の先頭と、2つの補助メソッド）

```csharp
#if TICK_PORT_EXPERIMENT
        if (generic.GetParameters().Length == 2 && generic.GetParameters()[1].ParameterType == typeof(MethodBase))
            return CreateWithOriginal(name, generic);
#endif
#if TICK_DISPATCH_EXPERIMENT
        if (generic.GetParameters().Length == 2) return CreateWithGenerator(name, generic);
#endif
```

`CreateWithOriginal(name, generic)`は`Func<IEnumerable<CodeInstruction>, MethodBase, IEnumerable<CodeInstruction>>`、
`CreateWithGenerator(name, generic)`は`Func<IEnumerable<CodeInstruction>, ILGenerator, IEnumerable<CodeInstruction>>`を受ける
動的型を`Create`と同じ形（`Rewrite`静的フィールド＋`Transpile`静的メソッド、`Ldsfld; Ldarg_0; Ldarg_1; Callvirt Invoke; Ret`）で生成し、
`Transpile`の引数名を`instructions`/`original`（前者）にする。

## src/T3MP/Runtime/TickFrontier.cs（TICK_PORT_EXPERIMENT 12箇所、TICK_DISPATCH_EXPERIMENT 1箇所）

```csharp
// Revalidate: _entityTick と各ガード対象の外部パッチ判定に実験ownerを許す
#if TICK_PORT_EXPERIMENT
        if (RuntimePatches.ForeignPatched(_harmonyType, _entityTick, new[] { Owner, TickComponentDispatch.Owner }, transpilersOnly: false)) return true;
#else
        if (RuntimePatches.ForeignPatched(_harmonyType, _entityTick, Owner, transpilersOnly: false)) return true;
#endif
        foreach (var method in _guardedAny)
        {
#if TICK_PORT_EXPERIMENT
            if (TickComponentDispatch.CompatibleFrontierPath(method))
            {
                if (RuntimePatches.ForeignPatched(_harmonyType, method, new[] { Owner, TickComponentDispatch.Owner }, transpilersOnly: false)) return true;
                continue;
            }
#endif
#if TICK_DISPATCH_EXPERIMENT
            if (TickDispatch.CompatibleFrontierPath(method))
            {
                if (RuntimePatches.ForeignPatched(_harmonyType, method, new[] { Owner, TickDispatch.Owner }, transpilersOnly: false)) return true;
                continue;
            }
#endif
            if (RuntimePatches.ForeignPatched(_harmonyType, method, Owner, transpilersOnly: false)) return true;

// AfterAdd / AfterRemove の先頭
#if TICK_PORT_EXPERIMENT
        TickComponentDispatch.MembershipChanged(__instance);
#endif

// BeforeEnable の __state = false の後、BeforeDisable の先頭
#if TICK_PORT_EXPERIMENT
        if (TickComponentDispatch.NeedsNativeTraversal) return;
#endif

// FinalizeEnable の先頭
#if TICK_PORT_EXPERIMENT
        if (TickComponentDispatch.NeedsNativeTraversal)
        {
            if (__state && Components.TryGetValue(__instance, out var pending) && pending.Pending > 0) pending.Pending--;
            __state = false;
            return __exception;
        }
#endif

// FinalizeDisable の先頭
#if TICK_PORT_EXPERIMENT
        if (TickComponentDispatch.NeedsNativeTraversal) return __exception;
#endif

// 追加メソッド
#if TICK_PORT_EXPERIMENT
    internal static void EnabledStored(BaseComponent component, bool value)
    {
        if (!Components.TryGetValue(component, out var state)) return;
        state.Current = value;
        Reconcile(state);
    }
#endif

// TickAllFast のバケット走査
#if TICK_PORT_EXPERIMENT
        var dispatch = TickComponentDispatch.Prepare(bucket);
#endif
        for (var i = 0; i < entities.Count; i++)
        {
#if TICK_PORT_EXPERIMENT
            var next = TickComponentDispatch.NeedsNativeTraversal ? i : index.NextIndex(entities, i);
#else
            var next = index.NextIndex(entities, i);
#endif
            i = next;
            if (i >= entities.Count) break;
            var entity = entities.Values[i];
#if TICK_PORT_EXPERIMENT
            if (!TickComponentDispatch.NeedsNativeTraversal && index.Awaiting > 0) index.ObserveActivation(entities.Keys[i], entity);
            if (dispatch != null) TickComponentDispatch.TickEntity(dispatch, i, entity);
            else entity.Tick();
#else
            if (index.Awaiting > 0) index.ObserveActivation(entities.Keys[i], entity);
            entity.Tick();
#endif
        }
        bucket._isTicking = false;
        var toRemove = bucket._entitiesToRemove;
#if TICK_PORT_EXPERIMENT
        if (toRemove.Count != 0) TickComponentDispatch.MembershipChanged(bucket);
#endif
```
