#nullable enable

using System;
using System.IO;
using System.Runtime.InteropServices;

using FancyWM.Utilities;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class AutostartTest
    {
        private string m_root = null!;
        private string m_executable = null!;
        private string m_link = null!;

        [TestInitialize]
        public void Setup()
        {
            m_root = Path.Combine(Path.GetTempPath(), "FancyWM portable тест " + Guid.NewGuid());
            Directory.CreateDirectory(m_root);
            m_executable = Path.Combine(m_root, "FancyWM-GUI.exe");
            File.WriteAllBytes(m_executable, []);
            m_link = Path.Combine(m_root, "Startup", "FancyWM.lnk");
        }

        [TestCleanup]
        public void Cleanup() => Directory.Delete(m_root, recursive: true);

        [TestMethod]
        public void PortableUsesAdjacentGuiEvenWhenCliIsAvailable()
        {
            File.WriteAllBytes(Path.Combine(m_root, "FancyWM.exe"), []);
            Assert.AreEqual(m_executable, Autostart.GetLegacyExecutablePath(m_root));
        }

        [TestMethod]
        public void CliOnlyDistributionUsesItsOwnExecutable()
        {
            File.Delete(m_executable);
            var cli = Path.Combine(m_root, "FancyWM.exe");
            File.WriteAllBytes(cli, []);
            Assert.AreEqual(cli, Autostart.GetLegacyExecutablePath(m_root));
        }

        [TestMethod]
        public void EnableCreatesRealShortcutWithAbsoluteTargetAndWorkingDirectory()
        {
            Assert.IsFalse(Autostart.IsEnabledLegacy(m_link, m_executable));
            Assert.IsTrue(Autostart.EnableLegacy(m_link, m_executable));
            ReadShortcut(link =>
            {
                Assert.AreEqual(m_executable, (string)link.TargetPath);
                Assert.AreEqual(m_root, (string)link.WorkingDirectory);
                Assert.AreEqual(string.Empty, (string)link.Arguments);
            });
            Assert.IsTrue(Autostart.IsEnabledLegacy(m_link, m_executable));
            Assert.IsTrue(Autostart.DisableLegacy(m_link));
            Assert.IsFalse(File.Exists(m_link));
            Assert.IsFalse(Autostart.IsEnabledLegacy(m_link, m_executable));
            Assert.IsTrue(Autostart.DisableLegacy(m_link));
        }

        [TestMethod]
        public void EnableReplacesStoreShortcutAndRemovesItsArguments()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(m_link)!);
            File.WriteAllBytes(m_link, Resources.Files.FancyWM_lnk);
            Assert.IsFalse(Autostart.IsEnabledLegacy(m_link, m_executable));
            Assert.IsTrue(Autostart.EnableLegacy(m_link, m_executable));
            ReadShortcut(link =>
            {
                Assert.AreEqual(m_executable, (string)link.TargetPath);
                Assert.AreEqual(string.Empty, (string)link.Arguments);
            });
        }

        [TestMethod]
        public void EnableUpdatesRegistrationAfterMovingPortable()
        {
            Assert.IsTrue(Autostart.EnableLegacy(m_link, m_executable));
            var movedDirectory = Path.Combine(m_root, "new portable location");
            Directory.CreateDirectory(movedDirectory);
            var movedExecutable = Path.Combine(movedDirectory, "FancyWM-GUI.exe");
            File.Move(m_executable, movedExecutable);
            Assert.IsFalse(Autostart.IsEnabledLegacy(m_link, movedExecutable));
            Assert.IsTrue(Autostart.EnableLegacy(m_link, movedExecutable));
            ReadShortcut(link => Assert.AreEqual(movedExecutable, (string)link.TargetPath));
        }

        [TestMethod]
        public void MissingExecutableIsNotEnabledAndDoesNotReplaceAnExistingLink()
        {
            Assert.IsTrue(Autostart.EnableLegacy(m_link, m_executable));
            var original = File.ReadAllBytes(m_link);
            File.Delete(m_executable);
            Assert.IsFalse(Autostart.IsEnabledLegacy(m_link, m_executable));
            Assert.ThrowsException<FileNotFoundException>(() => Autostart.EnableLegacy(m_link, m_executable));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(m_link));
        }

        private void ReadShortcut(Action<dynamic> inspect)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            object? link = null;
            try
            {
                link = ((dynamic)shell).CreateShortcut(m_link);
                inspect(link);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
