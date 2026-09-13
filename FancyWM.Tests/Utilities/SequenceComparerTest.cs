#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

using FancyWM.Models;
using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class SequenceComparerTest
    {
        [TestMethod]
        public void SequenceEqualityKeepsOrderDuplicatesAndNullSemantics()
        {
            var comparer = StringComparer.OrdinalIgnoreCase.ToSequenceComparer();
            Assert.IsTrue(comparer.Equals(null, null));
            Assert.IsFalse(comparer.Equals(null, Array.Empty<string>()));
            Assert.IsFalse(comparer.Equals(Array.Empty<string>(), null));
            Assert.IsTrue(comparer.Equals(Array.Empty<string>(), Array.Empty<string>()));
            Assert.IsTrue(comparer.Equals(["alpha", "BETA", "alpha"], ["ALPHA", "beta", "ALPHA"]));
            Assert.IsFalse(comparer.Equals(["alpha", "beta"], ["beta", "alpha"]));
            Assert.IsFalse(comparer.Equals(["alpha", "alpha"], ["alpha"]));
        }

        [TestMethod]
        public void HashUsesElementComparerAndSupportsNullElements()
        {
            var comparer = StringComparer.OrdinalIgnoreCase.ToSequenceComparer();
            string[] first = ["Alpha", null!, "BETA"];
            string[] second = ["ALPHA", null!, "beta"];
            Assert.IsTrue(comparer.Equals(first, second));
            Assert.AreEqual(comparer.GetHashCode(first), comparer.GetHashCode(second));

            var sequences = new HashSet<IEnumerable<string>?>(comparer) { first };
            Assert.IsTrue(sequences.Contains(second));
            Assert.IsFalse(sequences.Add(second));
            Assert.IsTrue(sequences.Add(["BETA", null!, "Alpha"]));
            Assert.AreEqual(2, sequences.Count);
        }

        [TestMethod]
        public void HashEnumeratesInputOnceWithoutChangingIt()
        {
            var sequence = new SinglePassSequence<int>([4, 1, 4]);
            var comparer = EqualityComparer<int>.Default.ToSequenceComparer();
            int actual = comparer.GetHashCode(sequence);
            Assert.AreEqual(comparer.GetHashCode(new[] { 4, 1, 4 }), actual);
            Assert.AreEqual(1, sequence.Enumerations);
            Assert.AreEqual(3, sequence.ValuesRead);
        }

        [TestMethod]
        public void DistinctSequencesDoNotAllShareOneHashBucket()
        {
            var comparer = EqualityComparer<int>.Default.ToSequenceComparer();
            var hashes = Enumerable.Range(0, 256)
                .Select(value => comparer.GetHashCode(new[] { value, value + 1 }))
                .Distinct()
                .Count();
            Assert.IsTrue(hashes >= 240, $"Only {hashes} hash buckets for 256 distinct ordered sequences.");
        }

        [TestMethod]
        public void DistinctSequencesAvoidQuadraticElementEqualityCalls()
        {
            var elements = new CountingComparer<int>();
            var sequences = new HashSet<IEnumerable<int>?>(elements.ToSequenceComparer());
            for (int value = 0; value < 256; value++)
            {
                Assert.IsTrue(sequences.Add(new[] { value, value + 1 }));
            }
            Assert.AreEqual(256, sequences.Count);
            Assert.IsTrue(elements.EqualityCalls < 256,
                $"Building 256 distinct patterns made {elements.EqualityCalls} element comparisons.");
        }

        [TestMethod]
        public void SequenceHashCounterScenario()
        {
            // Use the real supported defaults as well as a scaled stress input.
            // The same test assembly can run against the archived production DLL.
            var defaults = new KeybindingDictionary(useDefaults: true)
                .Values.Where(binding => binding != null)
                .Select(binding => binding!.Keys.ToArray())
                .ToArray();
            Assert.IsTrue(defaults.Length > 10);
            KeyCode[][] scaled = Enumerable.Range(0, 256)
                .Select(value => new[] { (KeyCode)value, (KeyCode)((value + 17) % 256) })
                .ToArray();

            foreach (var (name, patterns) in new[] { ("defaults", defaults), ("scaled", scaled) })
            {
                RunPatternSet(patterns, 20);
                var measured = RunPatternSet(patterns, 100);
                Console.WriteLine($"PERFCOUNTER sequence-hash-{name} equality-calls {measured.EqualityCalls}");
                Console.WriteLine($"PERFCOUNTER sequence-hash-{name} hash-calls {measured.HashCalls}");
                Console.WriteLine($"PERFCOUNTER sequence-hash-{name} patterns {patterns.Length}");
            }
        }

        private static CountingComparer<KeyCode> RunPatternSet(KeyCode[][] patterns, int iterations)
        {
            var elements = new CountingComparer<KeyCode>();
            var comparer = elements.ToSequenceComparer();
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                var sequences = new HashSet<IEnumerable<KeyCode>?>(comparer);
                foreach (var pattern in patterns)
                {
                    Assert.IsTrue(sequences.Add(pattern), "All source patterns are distinct and must be retained.");
                }
                foreach (var pattern in patterns)
                {
                    Assert.IsTrue(sequences.Contains(pattern.ToArray()), "Equivalent new arrays must find the same binding.");
                    Assert.IsFalse(sequences.Add(pattern.ToArray()), "Equivalent duplicate patterns must be rejected.");
                }
                Assert.AreEqual(patterns.Length, sequences.Count);
                foreach (var (expected, actual) in patterns.Zip(sequences))
                {
                    Assert.AreSame(expected, actual, "Hashing must preserve the surviving original pattern identity and order.");
                }
            }
            return elements;
        }

        private sealed class CountingComparer<T> : IEqualityComparer<T>
        {
            public int EqualityCalls { get; private set; }
            public int HashCalls { get; private set; }

            public bool Equals(T? x, T? y)
            {
                EqualityCalls++;
                return EqualityComparer<T>.Default.Equals(x, y);
            }

            public int GetHashCode(T obj)
            {
                HashCalls++;
                return EqualityComparer<T>.Default.GetHashCode(obj!);
            }
        }

        private sealed class SinglePassSequence<T>(IReadOnlyList<T> values) : IEnumerable<T>
        {
            public int Enumerations { get; private set; }
            public int ValuesRead { get; private set; }

            public IEnumerator<T> GetEnumerator()
            {
                if (++Enumerations != 1) { throw new InvalidOperationException("Input was enumerated twice."); }
                foreach (var value in values)
                {
                    ValuesRead++;
                    yield return value;
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
