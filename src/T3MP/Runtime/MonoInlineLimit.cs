using System;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

namespace T3MP.Runtime;

// Raises the Mono JIT's inline size limit for methods compiled from now on.
// The game's runtime (mono-2.0-bdwgc.dll) inlines a callee only when its IL is
// smaller than a static limit, 20 bytes by default; the simulation is made of
// small methods and property chains, so most calls stay real calls. The runtime
// reads the limit from the MONO_INLINELIMIT environment variable, but only once
// and before mods load, so a mod can only change the value in memory.
//
// Nothing on disk is touched and the change lasts for the process: methods
// already compiled keep their code, restoring 20 would not undo inlined copies,
// and the switch or removing the mod takes effect at the next launch.
//
// The variable is found through the runtime's own reader: the only code that
// loads the address of the "MONO_INLINELIMIT" string is followed by getenv,
// atoi and the store of the parsed value into the limit variable; the first
// rip-relative `mov [rip+disp32],eax` after it names that variable.
//
// Installed after T3MP's own patches: Harmony compiles methods while it patches,
// and a callee inlined into such a caller before its own patch would escape it.
// Measured on a 3,700-character colony: docs/evidence.json, mono-inline-limit-*.
internal static class MonoInlineLimit
{
    internal const int Limit = 300;
    internal static bool Installed { get; private set; }
    private static string _summary = "not attempted";
    internal static string Summary() => _summary;

    // Other platforms do not load this runtime; a user-set MONO_INLINELIMIT is
    // the user's choice. Anything else throws out of StartMod.
    internal static void Install()
    {
        _summary = Apply();
        Debug.Log("[T3MP] Mono inline limit: " + _summary);
    }

    private static Exception Fail(string reason) => new InvalidOperationException("T3MP: Mono inline limit could not be applied: " + reason);

    private static string Apply()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8) return "unsupported platform";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MONO_INLINELIMIT"))) return "left to MONO_INLINELIMIT";
        var module = GetModuleHandleW("mono-2.0-bdwgc.dll");
        if (module == IntPtr.Zero) throw Fail("runtime module not found");
        var header = Read(module, 0, 0x1000);
        var pe = BitConverter.ToInt32(header, 0x3C);
        int sections = BitConverter.ToUInt16(header, pe + 6), table = pe + 24 + BitConverter.ToUInt16(header, pe + 20);
        long textRva = 0, textSize = 0, rdataRva = 0, rdataSize = 0;
        for (var i = 0; i < sections; i++, table += 40)
        {
            var name = System.Text.Encoding.ASCII.GetString(header, table, 8).TrimEnd('\0');
            if (name == ".text") { textSize = BitConverter.ToUInt32(header, table + 8); textRva = BitConverter.ToUInt32(header, table + 12); }
            else if (name == ".rdata") { rdataSize = BitConverter.ToUInt32(header, table + 8); rdataRva = BitConverter.ToUInt32(header, table + 12); }
        }
        var rdata = Read(module, rdataRva, (int)rdataSize);
        var at = IndexOf(rdata, System.Text.Encoding.ASCII.GetBytes("MONO_INLINELIMIT\0"));
        if (at < 0) throw Fail("limit name not found");
        var nameRva = rdataRva + at;
        var text = Read(module, textRva, (int)textSize);
        for (var i = 0; i + 7 <= text.Length; i++)
        {
            // lea rcx,[rip+rel32] -> "MONO_INLINELIMIT"
            if (text[i] != 0x48 || text[i + 1] != 0x8D || text[i + 2] != 0x0D || textRva + i + 7 + BitConverter.ToInt32(text, i + 3) != nameRva) continue;
            for (var p = i + 7; p + 6 <= text.Length && p < i + 0x60; p++)
            {
                if (text[p] != 0x89 || text[p + 1] != 0x05) continue;
                var limitRva = textRva + p + 6 + BitConverter.ToInt32(text, p + 2);
                Marshal.WriteInt32(new IntPtr(module.ToInt64() + limitRva), Limit);
                Installed = true;
                return "limit at rva 0x" + limitRva.ToString("X") + " -> " + Limit;
            }
        }
        throw Fail("limit reader not found");
    }

    private static byte[] Read(IntPtr module, long rva, int size)
    {
        var buffer = new byte[size];
        Marshal.Copy(new IntPtr(module.ToInt64() + rva), buffer, 0, size);
        return buffer;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
}
