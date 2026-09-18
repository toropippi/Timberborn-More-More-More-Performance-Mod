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
// The variable is found through the runtime's own reader, and only when that
// reader matches the complete reviewed instruction template of the supported
// runtimes (game 1.1.2.4 and 1.0.13.1), byte for byte apart from displacements:
//   cmp [flag],0 / jne done / lea rcx,"MONO_INLINELIMIT" / call getenv /
//   mov rsi,rax / test rax,rax / jz default / mov rcx,rax / call atoi /
//   mov rcx,rsi / mov [limit],eax / mov [llvmjit],eax / mov [llvmaot],eax /
//   call free / jmp flagstore / default: mov [limit],20 / mov [llvmjit],100 /
//   mov [llvmaot],20 / flagstore: mov [flag],1 / done: mov eax,[limit]
// Both stores and the final load must name one limit variable, both flag
// references one flag, every branch must land on the expected instruction, all
// four variables must be distinct, aligned and inside committed read/write
// pages of .data, the flag must be 1 and the limit 20. Anything else leaves the
// runtime untouched. A user-provided MONO_INLINELIMIT is left alone.
//
// Installed after T3MP's own patches: Harmony compiles methods while it patches,
// and a callee inlined into such a caller before its own patch would escape it.
// Measured on a 3,700-character colony: docs/evidence.json, mono-inline-limit-*.
internal static class MonoInlineLimit
{
    internal const int Limit = 300;
    private const int DefaultLimit = 20;
    private const int MaxSection = 64 * 1024 * 1024;
    internal static bool Installed { get; private set; }
    internal static bool Active => Installed;
    private static string _summary = "not attempted";
    internal static string Summary() => _summary;

    internal static void Install()
    {
        try { _summary = Apply(); }
        catch (Exception exception) { _summary = "failed: " + exception.GetBaseException().Message; }
        Debug.Log("[T3MP] Mono inline limit: " + _summary);
    }

    private static string Apply()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8) return "unsupported platform";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MONO_INLINELIMIT"))) return "left to MONO_INLINELIMIT";
        var module = GetModuleHandleW("mono-2.0-bdwgc.dll");
        if (module == IntPtr.Zero) return "runtime module not found";
        var process = GetCurrentProcess();

        // PE32+ / AMD64 image with plausible .text, .rdata and .data sections.
        var header = Read(process, module, 0, 0x1000);
        if (header == null || header[0] != (byte)'M' || header[1] != (byte)'Z') return "no image header";
        var pe = BitConverter.ToInt32(header, 0x3C);
        if (pe <= 0 || pe > 0x800 || BitConverter.ToUInt32(header, pe) != 0x00004550) return "no PE header";
        if (BitConverter.ToUInt16(header, pe + 4) != 0x8664 || BitConverter.ToUInt16(header, pe + 24) != 0x20B) return "not an x64 image";
        long imageSize = BitConverter.ToUInt32(header, pe + 24 + 56);
        int sections = BitConverter.ToUInt16(header, pe + 6), table = pe + 24 + BitConverter.ToUInt16(header, pe + 20);
        long textRva = 0, textSize = 0, rdataRva = 0, rdataSize = 0, dataRva = 0, dataSize = 0;
        for (var i = 0; i < sections && table + 40 <= header.Length; i++, table += 40)
        {
            var name = System.Text.Encoding.ASCII.GetString(header, table, 8).TrimEnd('\0');
            long size = BitConverter.ToUInt32(header, table + 8), rva = BitConverter.ToUInt32(header, table + 12);
            if (name == ".text") { textRva = rva; textSize = size; }
            else if (name == ".rdata") { rdataRva = rva; rdataSize = size; }
            else if (name == ".data") { dataRva = rva; dataSize = size; }
        }
        foreach (var (rva, size) in new[] { (textRva, textSize), (rdataRva, rdataSize), (dataRva, dataSize) })
            if (rva <= 0 || size <= 0 || size > MaxSection || rva + size > imageSize) return "sections not plausible";

        var rdata = Read(process, module, rdataRva, (int)rdataSize);
        if (rdata == null) return "read-only data unreadable";
        var needle = System.Text.Encoding.ASCII.GetBytes("MONO_INLINELIMIT\0");
        var at = IndexOf(rdata, needle);
        if (at < 0 || IndexOf(rdata, needle, at + 1) >= 0) return "limit name not unique";
        var nameRva = rdataRva + at;

        var text = Read(process, module, textRva, (int)textSize);
        if (text == null) return "code unreadable";
        var site = -1;
        for (var i = 0; i + 7 <= text.Length; i++)
        {
            if (text[i] != 0x48 || text[i + 1] != 0x8D || text[i + 2] != 0x0D) continue;   // lea rcx,[rip+rel32]
            if (textRva + i + 7 + BitConverter.ToInt32(text, i + 3) != nameRva) continue;
            if (site >= 0) return "limit name referenced more than once";
            site = i;
        }
        if (site < 9 || site + 0x70 > text.Length) return "limit reader not found";

        // Complete template. `p` walks instruction by instruction from the cmp.
        long Target(int instruction, int length, int displacementAt) => textRva + instruction + length + BitConverter.ToInt32(text, instruction + displacementAt);
        bool Bytes(int offset, params byte[] expected)
        {
            for (var i = 0; i < expected.Length; i++) if (text[offset + i] != expected[i]) return false;
            return true;
        }
        var p = site - 9;
        if (!Bytes(p, 0x83, 0x3D) || text[p + 6] != 0x00) return "template: flag test";
        var flagA = Target(p, 7, 2);
        if (text[p + 7] != 0x75) return "template: skip branch";
        var done = site + (sbyte)text[p + 8];                                                // jne done
        p = site + 7;
        if (text[p] != 0xE8) return "template: getenv call";
        p += 5;
        if (!Bytes(p, 0x48, 0x8B, 0xF0, 0x48, 0x85, 0xC0, 0x74)) return "template: null test";
        var defaults = p + 8 + (sbyte)text[p + 7];                                           // jz default
        p += 8;
        if (!Bytes(p, 0x48, 0x8B, 0xC8, 0xE8)) return "template: atoi call";
        p += 8;
        if (!Bytes(p, 0x48, 0x8B, 0xCE)) return "template: free argument";
        p += 3;
        var stores = new long[3];
        for (var k = 0; k < 3; k++, p += 6)
        {
            if (!Bytes(p, 0x89, 0x05)) return "template: parsed stores";
            stores[k] = Target(p, 6, 2);
        }
        if (Bytes(p, 0xFF, 0x15)) p += 6; else if (text[p] == 0xE8) p += 5; else return "template: free call";
        if (text[p] != 0xEB) return "template: jump over defaults";
        var flagStore = p + 2 + (sbyte)text[p + 1];
        p += 2;
        if (p != defaults) return "template: default branch target";
        var defaultValues = new[] { DefaultLimit, 100, DefaultLimit };
        for (var k = 0; k < 3; k++, p += 10)
        {
            if (!Bytes(p, 0xC7, 0x05) || BitConverter.ToInt32(text, p + 6) != defaultValues[k]) return "template: default stores";
            if (Target(p, 10, 2) != stores[k]) return "template: default targets";
        }
        if (p != flagStore || !Bytes(p, 0xC7, 0x05) || BitConverter.ToInt32(text, p + 6) != 1) return "template: flag store";
        if (Target(p, 10, 2) != flagA) return "template: flag targets";
        p += 10;
        if (p != done || !Bytes(p, 0x8B, 0x05) || Target(p, 6, 2) != stores[0]) return "template: limit load";

        long limitRva = stores[0], flagRva = flagA;
        var variables = new[] { limitRva, stores[1], stores[2], flagRva };
        for (var i = 0; i < variables.Length; i++)
        {
            if (variables[i] < dataRva || variables[i] + 4 > dataRva + dataSize || (variables[i] & 3) != 0) return "variable outside .data or unaligned";
            for (var j = 0; j < i; j++) if (variables[j] == variables[i]) return "variables not distinct";
        }

        var limitAddress = new IntPtr(module.ToInt64() + limitRva);
        if (VirtualQuery(limitAddress, out var page, (IntPtr)Marshal.SizeOf<MemoryInfo>()) == IntPtr.Zero ||
            page.State != 0x1000 || (page.Protect != 0x04 && page.Protect != 0x08)) return "limit page is not committed read/write";   // MEM_COMMIT; PAGE_READWRITE or PAGE_WRITECOPY
        var flagBytes = Read(process, module, flagRva, 4);
        var limitBytes = Read(process, module, limitRva, 4);
        if (flagBytes == null || limitBytes == null) return "variables unreadable";
        var flag = BitConverter.ToInt32(flagBytes, 0);
        var current = BitConverter.ToInt32(limitBytes, 0);
        if (flag != 1) return "runtime has not read its limit yet (flag=" + flag + ")";
        if (current != DefaultLimit) return "limit is already " + current;
        Marshal.WriteInt32(limitAddress, Limit);
        var after = Read(process, module, limitRva, 4);
        if (after == null || BitConverter.ToInt32(after, 0) != Limit) return "write did not stick";
        Installed = true;
        return DefaultLimit + " -> " + Limit;
    }

    private static byte[]? Read(IntPtr process, IntPtr module, long rva, int size)
    {
        var buffer = new byte[size];
        return ReadProcessMemory(process, new IntPtr(module.ToInt64() + rva), buffer, (IntPtr)size, out var read) && read.ToInt64() == size ? buffer : null;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start = 0)
    {
        for (var i = start; i + needle.Length <= haystack.Length; i++)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        internal IntPtr BaseAddress, AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId, Reserved;
        internal IntPtr RegionSize;
        internal uint State, Protect, Type;
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("kernel32")] private static extern IntPtr VirtualQuery(IntPtr address, out MemoryInfo info, IntPtr length);
}
