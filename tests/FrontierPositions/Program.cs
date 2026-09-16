using System;
using System.Collections.Generic;
using System.Linq;
using T3MP.Runtime;

var random = new Random(9142026);
var comparisons = 0L;
Guid Key(int i) => new Guid(i, 0, 0, new byte[8]);
void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
var source = new SortedList<Guid, int>();
var needed = new HashSet<Guid>();
var candidate = new FrontierPositions<int>();
Check(FrontierPositions<int>.Supported, "Collection version accessor unsupported");
void Set(Guid key, bool value) { if (value) needed.Add(key); else needed.Remove(key); candidate.Set(key, value); }
void Add(Guid key, bool value) { source.Add(key, 0); candidate.MembershipChanged(); Set(key, value); }
void Remove(Guid key) { source.Remove(key); needed.Remove(key); candidate.Remove(key); }
int Reference(int cursor) {
    if (cursor < 0 || cursor >= source.Count) return cursor;
    for (var i = cursor; i < source.Count; i++) if (needed.Contains(source.Keys[i])) return i;
    return source.Count;
}
void AllCursors() {
    for (var i = -1; i <= source.Count + 1; i++) {
        Check(candidate.Next(source, i) == Reference(i), "Next index mismatch at " + i);
        comparisons++;
    }
}
AllCursors();
// Every bit position, word boundaries, tails, all empty and all eligible.
for (var i = 0; i < 193; i++) Add(Key(i * 2), false);
AllCursors();
for (var i = 0; i < source.Count; i++) { Set(source.Keys[i], true); AllCursors(); Set(source.Keys[i], false); }
foreach (var key in source.Keys) Set(key, true);
AllCursors();
// Same count replacement shifts source positions before the current cursor.
Remove(Key(0)); Add(Key(1), false); AllCursors();
Remove(Key(384)); Add(Key(-1), true); AllCursors();
// Random structural and live flag changes, including replacing one key at the
// same count, redundant notifications, cleared/reloaded worlds, and GUID order.
for (var step = 0; step < 12000; step++) {
    var key = Key(random.Next(-100, 1000));
    switch (random.Next(6)) {
        case 0: if (!source.ContainsKey(key)) Add(key, random.Next(2) == 0); break;
        case 1: Remove(key); break;
        case 2: if (source.ContainsKey(key)) Set(key, random.Next(2) == 0); break;
        case 3:
            if (source.Count > 0 && !source.ContainsKey(key)) { Remove(source.Keys[random.Next(source.Count)]); Add(key, true); }
            break;
        case 4:
            if (source.Count > 0) { var old = source.Keys[random.Next(source.Count)]; Set(old, needed.Contains(old)); }
            break;
        case 5:
            if (step % 41 == 0) { source.Clear(); needed.Clear(); candidate.Clear(); }
            break;
    }
    AllCursors();
}
// Independent buckets with shared entity ids must not share eligibility.
var other = new FrontierPositions<int>();
var otherSource = new SortedList<Guid, int> { { Key(0), 0 }, { Key(1), 0 } };
other.Set(Key(1), true);
Check(other.Next(otherSource, 0) == 1, "Independent bucket");
// Unknown eligible member falls back to visiting the cursor, then recovers.
other.Set(Key(4), true);
Check(other.Next(otherSource, 0) == 0, "Unknown member must not skip");
other.Remove(Key(4));
Check(other.Next(otherSource, 0) == 1, "Unknown member recovery");
// Direct same-count source edit, without the bucket membership hook: the
// eligible key moves to index zero. Count-only invalidation would miss it.
otherSource.Remove(Key(0)); otherSource.Add(Key(3), 0);
Check(other.Next(otherSource, 0) == 0, "Direct same-count mutation was not observed");
var replacedSource = new SortedList<Guid, int> { { Key(0), 0 }, { Key(1), 0 } };
replacedSource[Key(0)] = 1; replacedSource[Key(1)] = 1; // same count and native version
Check(other.Next(replacedSource, 0) == 1, "Source identity change was not observed");

// Simulate native numeric cursor semantics, including an insertion behind the
// cursor (which can revisit an entity), insertion ahead, enabling/disabling a
// future entity, and deferred removal (which remains tickable until sweep end).
List<Guid> Sweep(bool optimized) {
    var list = new SortedList<Guid, int>();
    var flags = new HashSet<Guid>();
    var index = new FrontierPositions<int>();
    foreach (var i in new[] { 10, 30, 50, 70, 90 }) { list.Add(Key(i), 0); flags.Add(Key(i)); index.Set(Key(i), true); }
    var trace = new List<Guid>();
    var removals = new List<Guid>();
    var changed = false;
    for (var cursor = 0; cursor < list.Count; cursor++) {
        if (optimized) cursor = index.Next(list, cursor);
        if (cursor >= list.Count) break;
        var key = list.Keys[cursor];
        if (!flags.Contains(key)) continue; // empty native entity loop
        trace.Add(key);
        if (key == Key(30) && !changed) {
            changed = true;
            foreach (var i in new[] { 20, 40 }) { list.Add(Key(i), 0); index.MembershipChanged(); flags.Add(Key(i)); index.Set(Key(i), true); }
            flags.Remove(Key(50)); index.Set(Key(50), false);
            removals.Add(Key(70));
        }
        if (key == Key(40)) { flags.Add(Key(50)); index.Set(Key(50), true); }
        Check(trace.Count < 20, "Cursor did not advance");
    }
    foreach (var key in removals) { list.Remove(key); flags.Remove(key); index.Remove(key); }
    Check(index.Next(list, 0) == 0, "Deferred removal reconciliation");
    return trace;
}
var nativeTrace = Sweep(false);
Check(nativeTrace.SequenceEqual(Sweep(true)), "In-sweep mutation trace differs");
Check(nativeTrace.Count(k => k == Key(30)) == 2 && nativeTrace.Contains(Key(70)), "Fixture must revisit shifted entity and retain deferred removal");
Console.WriteLine("PASS: " + comparisons + " cursor comparisons; every bit, membership shifts, live toggles, reload, independent buckets, in-sweep native trace and deferred removal.");
