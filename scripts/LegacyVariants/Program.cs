using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

// Exact-byte experiment: only ldc.i4.1 -> ldc.i4.0 immediately preceding an
// allowlisted BenchmarkSettings field initializer. Never rebuild legacy code.
// MVID stays original; SHA256 and the live boolean settings identify variants.
if (args.Length != 3) throw new ArgumentException("LegacyVariants <original Code.dll> <groups.json> <new output directory>");
var original = File.ReadAllBytes(args[0]);
var originalHash = Convert.ToHexString(SHA256.HashData(original));
if (originalHash != "825B95FA2DAEAFE82B9B54B4F94F4B3EFB02BCB98025EEF4AF1C3191320B16FD")
    throw new InvalidOperationException("Not the audited Workshop 1.1.7 binary");
if (Directory.Exists(args[2])) throw new IOException("Output directory must be new");
using var pe = new PEReader(new MemoryStream(original));
var md = pe.GetMetadataReader();
var settings = md.TypeDefinitions.Select(md.GetTypeDefinition).Single(t =>
    md.GetString(t.Namespace) == "T3MP" && md.GetString(t.Name) == "BenchmarkSettings");
var cctor = settings.GetMethods().Select(md.GetMethodDefinition).Single(m => md.GetString(m.Name) == ".cctor");
var section = pe.PEHeaders.SectionHeaders.Single(s => cctor.RelativeVirtualAddress >= s.VirtualAddress &&
    cctor.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
var header = section.PointerToRawData + cctor.RelativeVirtualAddress - section.VirtualAddress;
var start = header + ((original[header] & 3) == 2 ? 1 : ((original[header + 1] >> 4) * 4));
var il = pe.GetMethodBody(cctor.RelativeVirtualAddress).GetILBytes()!;
if (!original.AsSpan(start, il.Length).SequenceEqual(il)) throw new InvalidOperationException("IL offset mismatch");
var opcodes = typeof(OpCodes).GetFields().Where(f => f.FieldType == typeof(OpCode))
    .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => unchecked((ushort)o.Value));
var initializers = new Dictionary<string, (int Offset, bool Value)>();
var previous = -1;
for (int i = 0; i < il.Length;)
{
    int offset = i;
    ushort key = il[i++];
    if (key == 0xfe) key = (ushort)(0xfe00 | il[i++]);
    var op = opcodes[key];
    if (op == OpCodes.Stsfld && previous >= 0 && (il[previous] == 0x16 || il[previous] == 0x17) && i - previous == 2)
    {
        var handle = MetadataTokens.EntityHandle(BitConverter.ToInt32(il, i));
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            var field = md.GetFieldDefinition((FieldDefinitionHandle)handle);
            if (field.GetDeclaringType() == settings.GetMethods().Select(h => md.GetMethodDefinition(h).GetDeclaringType()).First())
                initializers.Add(md.GetString(field.Name), (start + previous, il[previous] == 0x17));
        }
    }
    i += op.OperandType switch {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
        _ => 4
    };
    previous = offset;
}
var groups = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(args[1]))!;
var variants = new List<object>();
Directory.CreateDirectory(args[2]);
foreach (var (name, disabled) in groups)
{
    if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid group name");
    if (disabled.Distinct().Count() != disabled.Length) throw new ArgumentException("Duplicate setting");
    var bytes = (byte[])original.Clone();
    foreach (var field in disabled)
    {
        if (!initializers.TryGetValue(field, out var site) || !site.Value || !field.StartsWith("Enable"))
            throw new InvalidOperationException("Expected a constant true setting: " + field);
        bytes[site.Offset] = 0x16;
    }
    var differences = Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != original[i]).ToArray();
    if (differences.Length != disabled.Length || differences.Any(i => original[i] != 0x17 || bytes[i] != 0x16))
        throw new InvalidOperationException("Unexpected byte difference");
    var path = Path.GetFullPath(Path.Combine(args[2], name + ".dll"));
    File.WriteAllBytes(path, bytes);
    variants.Add(new { Name = name, Path = path, Hash = Convert.ToHexString(SHA256.HashData(bytes)), Disabled = disabled,
        ChangedOffsets = differences, ExpectedSettings = string.Join(";", initializers.Where(p => p.Key.StartsWith("Enable"))
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + (p.Value.Value && !disabled.Contains(p.Key)))) });
}
File.WriteAllText(Path.Combine(args[2], "variants.json"), JsonSerializer.Serialize(new {
    OriginalHash = originalHash, Mvid = md.GetGuid(md.GetModuleDefinition().Mvid), Variants = variants
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Verified {variants.Count} variants; each differs only at its listed boolean initializers.");
