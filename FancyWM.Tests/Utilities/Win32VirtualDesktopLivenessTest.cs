using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using WinMan.Windows;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class Win32VirtualDesktopLivenessTest
    {
        [DataTestMethod]
        [DataRow(1)]
        [DataRow(4)]
        [DataRow(10)]
        public void IsAliveReflectsRemovalAndReadditionWithoutAllocatingDesktopSnapshots(int desktopCount)
        {
            var fixture = new LivenessFixture(desktopCount);
            var original = fixture.Desktops[0];
            var publishedSnapshot = fixture.Manager.Desktops;

            Assert.IsTrue(original.IsAlive);
            Assert.IsTrue(fixture.Remove(original));
            Assert.IsFalse(original.IsAlive);

            var replacement = fixture.CreateDesktop(fixture.Guids[0]);
            fixture.Add(replacement);
            Assert.IsFalse(original.IsAlive,
                "A new wrapper with the same GUID must not revive the removed wrapper.");
            Assert.IsTrue(replacement.IsAlive);
            Assert.AreEqual(desktopCount, publishedSnapshot.Count,
                "The public desktop list must remain a stable caller-owned snapshot.");
            Assert.AreSame(original, publishedSnapshot[0]);

            Assert.IsTrue(fixture.Remove(replacement));
            fixture.Add(original);
            Assert.IsTrue(original.IsAlive);

            int warmLiveReads = 0;
            _ = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 10000; iteration++)
            {
                if (original.IsAlive) { warmLiveReads++; }
            }
            Assert.AreEqual(10000, warmLiveReads);

            int liveReads = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int iteration = 0; iteration < 10000; iteration++)
            {
                if (original.IsAlive) { liveReads++; }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.AreEqual(10000, liveReads);
            Assert.AreEqual(0L, allocated,
                "A liveness lookup must inspect current manager membership without allocating a public desktop snapshot.");
        }

        [TestMethod]
        public void ClosedWorkspaceStillRejectsLivenessRead()
        {
            var fixture = new LivenessFixture(1);
            fixture.CloseWorkspace();
            Assert.ThrowsException<InvalidOperationException>(() => _ = fixture.Desktops[0].IsAlive);
        }

        private sealed class LivenessFixture
        {
            private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
            private static readonly FieldInfo DescriptorField = typeof(Win32VirtualDesktop)
                .GetField("m_desktop", InstanceFields)!;
            private static readonly Type DescriptorType = DescriptorField.FieldType;

            private readonly object m_syncRoot = new();
            private readonly List<Win32VirtualDesktop> m_desktops = new();
            private readonly Win32Workspace m_workspace;

            public Win32VirtualDesktopManager Manager { get; }
            public IReadOnlyList<Win32VirtualDesktop> Desktops => m_desktops;
            public IReadOnlyList<Guid> Guids { get; }

            public LivenessFixture(int desktopCount)
            {
                m_workspace = Uninitialized<Win32Workspace>();
                Manager = Uninitialized<Win32VirtualDesktopManager>();
                SetField(Manager, "m_syncRoot", m_syncRoot);
                SetField(Manager, "m_desktops", m_desktops);
                SetField(m_workspace, "m_eventLoopThread", Thread.CurrentThread);
                SetField(m_workspace, "m_virtualDesktops", Manager);

                var guids = new List<Guid>(desktopCount);
                for (int index = 0; index < desktopCount; index++)
                {
                    var guid = Guid.NewGuid();
                    guids.Add(guid);
                    m_desktops.Add(CreateDesktop(guid));
                }
                Guids = guids;
            }

            public Win32VirtualDesktop CreateDesktop(Guid guid)
            {
                var desktop = Uninitialized<Win32VirtualDesktop>();
                SetField(desktop, "m_workspace", m_workspace);
                DescriptorField.SetValue(desktop, Activator.CreateInstance(DescriptorType, guid)!);
                return desktop;
            }

            public void Add(Win32VirtualDesktop desktop)
            {
                lock (m_syncRoot) { m_desktops.Add(desktop); }
            }

            public bool Remove(Win32VirtualDesktop desktop)
            {
                lock (m_syncRoot) { return m_desktops.Remove(desktop); }
            }

            public void CloseWorkspace() => m_workspace.GetType()
                .GetField("m_eventLoopThread", InstanceFields)!.SetValue(m_workspace, null);

            private static T Uninitialized<T>() where T : class =>
                (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

            private static void SetField(object owner, string name, object value) =>
                owner.GetType().GetField(name, InstanceFields)!.SetValue(owner, value);
        }
    }
}
