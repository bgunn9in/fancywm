using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace FancyWM.Utilities
{
    internal static class Collections
    {
        private class SequenceComparer<T>(IEqualityComparer<T> comparer) : IEqualityComparer<IEnumerable<T>?>
        {
            public IEqualityComparer<T> Comparer { get; } = comparer;

            public bool Equals([AllowNull] IEnumerable<T> x, [AllowNull] IEnumerable<T> y)
            {
                if (x == null)
                    return y == null;
                else if (y == null)
                    return x == null;

                return x.SequenceEqual(y, Comparer);
            }

            public int GetHashCode([DisallowNull] IEnumerable<T> obj)
            {
                var hash = new HashCode();
                foreach (var item in obj)
                {
                    hash.Add(item, Comparer);
                }
                return hash.ToHashCode();
            }
        }

        public static (IEnumerable<T> addList, IEnumerable<T> removeList, IEnumerable<T> persistList) Changes<T>(this IEnumerable<T> enumerable, IEnumerable<T> newEnumerable)
        {
            return enumerable.Changes(newEnumerable, EqualityComparer<T>.Default);
        }

        public static (IEnumerable<T> addList, IEnumerable<T> removeList, IEnumerable<T> persistList) Changes<T>(this IEnumerable<T> enumerable, IEnumerable<T> newEnumerable, IEqualityComparer<T> equalityComparer)
        {
            ArgumentNullException.ThrowIfNull(enumerable);
            ArgumentNullException.ThrowIfNull(newEnumerable);

            var previous = new HashSet<T>(equalityComparer);
            List<T> previousOrder = [];
            foreach (var item in enumerable)
            {
                if (previous.Add(item)) { previousOrder.Add(item); }
            }

            var next = new HashSet<T>(equalityComparer);
            List<T> added = [];
            foreach (var item in newEnumerable)
            {
                if (next.Add(item) && !previous.Contains(item)) { added.Add(item); }
            }

            List<T> removed = [];
            List<T> persisted = [];
            foreach (var item in previousOrder)
            {
                if (next.Contains(item)) { persisted.Add(item); }
                else { removed.Add(item); }
            }
            return (added, removed, persisted);
        }

        public static IEnumerable<(K Key, V Value)> AsPairs<K, V>(this IEnumerable<KeyValuePair<K, V>> keyValuePairs)
        {
            foreach (var kvp in keyValuePairs)
            {
                yield return (kvp.Key, kvp.Value);
            }
        }

        public static IEqualityComparer<IEnumerable<T>?> ToSequenceComparer<T>(this IEqualityComparer<T> comparer)
        {
            return new SequenceComparer<T>(comparer);
        }

        public static int IndexOf<T>(this IReadOnlyList<T> list, T value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (Equals(value, list[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        public static int IndexOf<T>(this IReadOnlyList<T> list, Predicate<T> pred)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (pred(list[i]))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
