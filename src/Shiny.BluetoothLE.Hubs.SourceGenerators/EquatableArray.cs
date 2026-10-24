using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Shiny.BluetoothLE.Hubs.SourceGenerators;

/// <summary>
/// Value equality for arrays so incremental pipeline models cache properly
/// </summary>
readonly struct EquatableArray<T>(T[]? items) : IEquatable<EquatableArray<T>>, IEnumerable<T> where T : IEquatable<T>
{
    readonly T[]? items = items;

    public T[] Items => this.items ?? [];
    public int Count => this.Items.Length;

    public bool Equals(EquatableArray<T> other) => this.Items.SequenceEqual(other.Items);
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && this.Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var item in this.Items)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)this.Items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

    public static implicit operator EquatableArray<T>(T[] items) => new(items);
}
