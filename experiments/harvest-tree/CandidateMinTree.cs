using System;
using System.Collections.Generic;

namespace T3MP.Runtime;

// Per-query tournament segment tree. Entries are already the native nearest
// candidates per good. Equal keys retain the first input, like SortedSet.Add.
// No eligibility or path result survives Clear(). Only empty buffers are reused.
internal sealed class CandidateMinTree<T> where T : struct, IComparable<T>
{
    private T[] _items = Array.Empty<T>();
    private int[] _winners = Array.Empty<int>();
    private int _count, _size;
    private bool _hasPrevious;
    private T _previous;
    internal int Count => _count;
    internal T Item(int i) => _items[i];

    internal void Build(IEnumerable<T> items)
    {
        Clear();
        foreach (var item in items)
        {
            if (_count == _items.Length) Array.Resize(ref _items, Math.Max(4, _count * 2));
            _items[_count++] = item;
        }
        _size = 1;
        while (_size < _count) _size *= 2;
        if (_winners.Length < _size * 2) _winners = new int[_size * 2];
        for (var i = 0; i < _size; i++) _winners[_size + i] = i < _count ? i : -1;
        for (var i = _size - 1; i > 0; i--) _winners[i] = Better(_winners[2 * i], _winners[2 * i + 1]);
    }

    private int Better(int left, int right)
    {
        if (left < 0) return right;
        if (right < 0) return left;
        return _items[left].CompareTo(_items[right]) <= 0 ? left : right;
    }

    internal bool TryPop(out T item)
    {
        while (_size > 0 && _winners[1] >= 0)
        {
            var winner = _winners[1];
            item = _items[winner];
            var node = _size + winner;
            _winners[node] = -1;
            for (node /= 2; node > 0; node /= 2)
                _winners[node] = Better(_winners[node * 2], _winners[node * 2 + 1]);
            if (_hasPrevious && _previous.CompareTo(item) == 0) continue;
            _hasPrevious = true;
            _previous = item;
            return true;
        }
        item = default;
        return false;
    }

    internal void Clear()
    {
        Array.Clear(_items, 0, _count);
        _count = _size = 0;
        _hasPrevious = false;
        _previous = default;
    }
}
