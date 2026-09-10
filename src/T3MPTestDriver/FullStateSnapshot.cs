using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Bindito.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace T3MPTestDriver;

// Runs after the scene timer, before smoke ticks. Uses the normal save writers
// in memory, without finishing a tick or touching the save repository.
internal static class FullStateSnapshot
{
    internal static void Report(IContainer container)
    {
        var args = Environment.GetCommandLineArgs();
        if (!args.Contains("-t3mpTestFullStateSnapshot")) return;
        var index = Array.IndexOf(args, "-logFile");
        var directory = Path.GetDirectoryName(args[index + 1])!;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Timberborn.GameSaveRuntimeSystem.GameSaver")).First(t => t != null)!;
        var saver = container.GetInstance(type);
        var write = type.GetMethod("SaveWithoutFinishingTick")!;
        var savedRandomState = UnityEngine.Random.state;
        var randomBefore = JsonUtility.ToJson(savedRandomState);
        var writerConsumedRandom = false;
        var timer = Stopwatch.StartNew();
        foreach (var name in new[] { "state-first.timber", "state-repeat.timber" })
        {
            try
            {
                using var stream = new MemoryStream();
                write.Invoke(saver, new object[] { stream });
                var bytes = stream.ToArray(); // Valid even if the writer closed the stream.
                File.WriteAllBytes(Path.Combine(directory, name), bytes);
                Debug.Log($"[T3MPFULLSTATE] file={name} bytes={bytes.Length}");
            }
            finally
            {
                // DateSalter.Save draws random values for the mod/dev-state
                // salts. Preserve those exported fields; restore the diagnostic
                // caller's RNG state so a snapshot does not advance simulation input.
                writerConsumedRandom |= JsonUtility.ToJson(UnityEngine.Random.state) != randomBefore;
                UnityEngine.Random.state = savedRandomState;
            }
        }
        var randomAfter = JsonUtility.ToJson(UnityEngine.Random.state);
        if (randomBefore != randomAfter) throw new InvalidOperationException("Save snapshot random state restoration failed");
        Debug.Log($"[T3MPFULLSTATE] ms={timer.Elapsed.TotalMilliseconds:F3} writerConsumedRandom={writerConsumedRandom} randomRestored=True; post-load diagnostic");
    }
}
