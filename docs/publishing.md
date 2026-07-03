# Publishing "More More More Performance!" — kit & procedure

Display name: **More More More Performance! (T3MP)**. Mod `Id` is `T3MP` and the
internal codename (assembly / namespace / folder / log prefix) is `T3MP` too.
The `Id` is now finalized — do **not** change it again: an Id change disables the
mod for existing users.

Tested on Timberborn game **1.1.0.2** (Unity 6000.4.6f1). The mod uses Harmony
patches on game internals, so it is version-sensitive: re-test and bump
`MinimumGameVersion` after each game update.

## 1. Build the distributable

```powershell
dotnet build .\src\T3MP\T3MP.csproj -c Release
.\scripts\deploy.ps1
```

The game-assembly path is resolved by `Directory.Build.props` (auto-detects a
Steam install; override with the `TIMBERBORN_DIR` env var) — no per-machine paths
are hard-coded in the `.csproj`.

`deploy.ps1` places the shippable mod at
`Documents\Timberborn\Mods\T3MP\`. That folder is the
distributable and must contain exactly:

- `manifest.json`
- `README.md`
- the built `Code.dll`

Nothing else (no benchmark logs, no `.pdb` unless you want stack traces). The
mod folder name can stay as-is; players never see it.

> Internal codename: the folder / assembly / namespace / log prefix are all
> `T3MP` (unified with the mod `Id`). Players never see it in-game (they only see
> the manifest `Name`); it appears in the public GitHub repo, in `Player.log`
> (`[T3MP] Loaded.`), and as the source folder. The compiled assembly is
> `Code.dll` by Timberborn convention. Everything is consistent — no rename
> pending.

## 2. Publish

Timberborn distributes through two platforms. You can do either or both; the
in-game **Mod Manager** loads mods from both `Documents\Timberborn\Mods` (local /
mod.io) and the Steam Workshop folder.

### Steam Workshop (has an in-game uploader)
1. Launch the game with the mod present in `Documents\Timberborn\Mods\`.
2. Main menu → **Mod Manager** → select the mod → **Upload** panel.
3. Fill name / description (see §3) / thumbnail / visibility, accept the Steam
   ToS on first use, upload. A `workshop_data.json` is generated to track the
   Steam item id for future updates ("Upload as new" makes a fresh listing).

### mod.io (no in-game uploader — website upload)
1. Zip the mod folder's **contents** (`manifest.json`, `README.md`, `Code.dll`)
   — select the files, not the parent folder.
   - Alternatively the official Unity **Mod Builder** (`Timberborn → Show Mod
     Builder`, Clean build) can emit a mod.io-ready zip, if you set the mod up
     in the Unity modding project.
2. Go to <https://mod.io/g/timberborn> → sign in → **Add mod**.
3. Upload the zip, paste the description (§3), add tags (Performance /
   Utilities), set the dependency note (requires Harmony), publish.

### After publishing
- Verify via the real distribution path (subscribe/download through the in-game
  Mod Manager on a clean profile) — not just the local `deploy.ps1` copy.
- On each Timberborn update: re-test, fix any broken Harmony patch, bump
  `Version` and `MinimumGameVersion`, re-upload.

## 3. Store description (ready to paste)

> **More More More Performance! (T3MP)**
>
> A **real, algorithmic speedup** of Timberborn's CPU-bound simulation — **not** a
> speed multiplier, **not** a benchmark tool. It rebuilds the heaviest hot paths
> so high game speed and large late-game colonies actually keep up, computing the
> **same** result faster. On a large late-game test colony the always-on
> optimizations run the sim about **1.5x** the unmodded rate at the same game
> speed (measured per full in-game day so day/night load is averaged); with the
> optional Shift+P turbo it reaches about **1.75x**.
>
> It does **not** change game speed — keep using the base game's speed buttons or
> any speed mod you like; this makes those actually deliver instead of
> stuttering. Behavior-identical to vanilla: **no farmer bugs, no save risk.**
>
> **Two features**
> - **Always-on optimizations** — turn on automatically a few seconds after a
>   save loads. Nothing to configure.
> - **Shift+P — turbo rendering (for unattended fast-forward)** — toggles a
>   render blackout + animation thinning. This only raises the tick rate when
>   the simulation is already the bottleneck — i.e. when you push the game well
>   past its normal top speed with a speed mod and the base is big enough that
>   ticks can't keep up. In that case it reclaims per-frame render time for the
>   sim (measured **+10–18%** at very high speeds on a large base). At the base
>   game's normal speeds it does **not** change the tick rate (the sim isn't
>   waiting on rendering), it just darkens the screen — so use it for leaving a
>   heavy colony fast-forwarding unattended, not for everyday play. The screen
>   goes dark (one frame is drawn every 100 ticks so you can watch progress);
>   press Shift+P again to restore rendering. A live tick-rate meter in the
>   bottom-right keeps updating so you can see the sim is still running.
>
> **Behavior-exact, and verified.** Every optimization was profiled on the
> actual hot paths (per-entity tick dispatch, need/behavior candidate
> selection, a speed-normalized travel-distance cache, render/UI suppression)
> and checked against an unmodified run to produce an **identical** colony.
> Nothing about pathfinding, reservations, inventories or water is skipped or
> approximated.
>
> **Requires the Harmony mod.** Tested on game 1.1.0.2.

## Sources (platform facts)
- Timberborn mod management & in-game upload: <https://github.com/mechanistry/timberborn-modding/wiki>
- mod.io platform page: <https://mod.io/g/timberborn>
- Mod Builder (zip for mod.io): <https://github.com/mechanistry/timberborn-modding/wiki/Mod-Builder>
- manifest.json fields / community guide: <https://datvm.github.io/TimberbornMods/ModdingGuide/getting-started.html>
