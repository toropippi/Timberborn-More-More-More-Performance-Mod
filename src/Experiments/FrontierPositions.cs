using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace T3MP.Runtime;

// Experimental build only. Stores eligibility, never simulation results.
// Membership changes invalidate positions immediately; eligibility changes
// update the current bit in place. SortedList remains the traversal authority.
internal sealed class FrontierPositions<T>
{
    private static readonly Func<SortedList<Guid, T>, int>? ReadVersion = CreateVersionReader();
    internal static bool Supported => ReadVersion != null;
    private readonly HashSet<Guid> _needed = new HashSet<Guid>();
    private readonly Dictionary<Guid, int> _positions = new Dictionary<Guid, int>();
    private ulong[] _words = Array.Empty<ulong>();
    private bool _dirty = true;
    private int _count;
    private int _version;
    private SortedList<Guid, T>? _source;

    internal void MembershipChanged() => _dirty = true;

    internal void Set(Guid key, bool needed)
    {
        if (needed) _needed.Add(key); else _needed.Remove(key);
        if (_dirty) return;
        if (!_positions.TryGetValue(key, out var position)) { _dirty = true; return; }
        var bit = 1UL << (position & 63);
        if (needed) _words[position >> 6] |= bit;
        else _words[position >> 6] &= ~bit;
    }

    internal void Remove(Guid key)
    {
        _needed.Remove(key);
        _dirty = true;
    }

    internal void Clear()
    {
        _needed.Clear();
        _positions.Clear();
        _dirty = true;
    }

    internal int Next(SortedList<Guid, T> source, int cursor)
    {
        if (cursor < 0 || cursor >= source.Count) return cursor;
        // Native collection version also observes edits made directly by other
        // mods, including same-count replacement. It is not a frame/tick key.
        var version = ReadVersion!(source);
        if (_dirty || _count != source.Count || _version != version || !ReferenceEquals(_source, source))
        {
            _dirty = true;
            _positions.Clear();
            _count = source.Count;
            var length = (_count + 63) >> 6;
            if (_words.Length != length) _words = new ulong[length];
            else Array.Clear(_words, 0, length);
            for (var i = 0; i < _count; i++)
            {
                var key = source.Keys[i];
                _positions.Add(key, i);
                if (_needed.Contains(key)) _words[i >> 6] |= 1UL << (i & 63);
            }
            // An unknown eligible key means the membership bookkeeping is not
            // authoritative. Do not skip anything until it is reconciled.
            foreach (var key in _needed)
                if (!_positions.ContainsKey(key)) return cursor;
            _source = source;
            _version = version;
            _dirty = false;
        }
        var wordIndex = cursor >> 6;
        var word = _words[wordIndex] & (ulong.MaxValue << (cursor & 63));
        while (true)
        {
            if (word != 0) return (wordIndex << 6) + TrailingZeroCount(word);
            if (++wordIndex >= _words.Length) return source.Count;
            word = _words[wordIndex];
        }
    }

    private static Func<SortedList<Guid, T>, int>? CreateVersionReader()
    {
        try
        {
            var type = typeof(SortedList<Guid, T>);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var field = type.GetField("version", flags) ?? type.GetField("_version", flags);
            if (field == null || field.FieldType != typeof(int)) return null;
            var method = new DynamicMethod("T3MPFrontierCollectionVersion", typeof(int), new[] { type }, typeof(FrontierPositions<T>).Module, true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, field);
            il.Emit(OpCodes.Ret);
            var reader = (Func<SortedList<Guid, T>, int>)method.CreateDelegate(typeof(Func<SortedList<Guid, T>, int>));
            // Verify the runtime field tracks the native writes on which the
            // index relies before any game method is patched.
            var probe = new SortedList<Guid, T>();
            var initial = reader(probe);
            probe.Add(Guid.Empty, default!);
            var added = reader(probe);
            probe.Remove(Guid.Empty);
            var removed = reader(probe);
            probe.Clear();
            if (added == initial || removed == added || reader(probe) == removed) return null;
            return reader;
        }
        catch (Exception) { return null; } // Unsupported runtime: install stays vanilla.
    }

    // Portable to the game's Mono/netstandard2.1; input is known nonzero.
    private static int TrailingZeroCount(ulong value)
    {
        var result = 0;
        if ((value & 0xffffffffUL) == 0) { result += 32; value >>= 32; }
        if ((value & 0xffffUL) == 0) { result += 16; value >>= 16; }
        if ((value & 0xffUL) == 0) { result += 8; value >>= 8; }
        if ((value & 0xfUL) == 0) { result += 4; value >>= 4; }
        if ((value & 3UL) == 0) { result += 2; value >>= 2; }
        if ((value & 1UL) == 0) result++;
        return result;
    }
}
