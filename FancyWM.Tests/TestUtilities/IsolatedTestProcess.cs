#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.TestUtilities
{
    internal static class IsolatedTestProcess
    {
        public static async Task RunAsync(TestContext testContext, Type fixture, string testName,
            string childFlag, string childValue, string artifactName,
            IReadOnlyDictionary<string, string>? childEnvironment = null)
        {
            string results = Path.Combine(testContext.TestRunDirectory, artifactName + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(results);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(fixture.Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" + testName);
            start.ArgumentList.Add("/Logger:trx;LogFileName=" + artifactName + ".trx");
            start.ArgumentList.Add("/ResultsDirectory:" + results);
            start.Environment[childFlag] = childValue;
            if (childEnvironment != null)
                foreach (var entry in childEnvironment) start.Environment[entry.Key] = entry.Value;
            using var child = Process.Start(start)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var error = child.StandardError.ReadToEndAsync();
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch (TimeoutException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"The owned {artifactName} process exceeded 45 seconds.");
            }
            string stdout = await output;
            string stderr = await error;
            string reportPath = Path.Combine(results, artifactName + ".trx");
            Assert.IsTrue(File.Exists(reportPath), stdout + Environment.NewLine + stderr);
            testContext.AddResultFile(reportPath);
            var report = XDocument.Load(reportPath);
            var counters = report.Descendants().Single(element => element.Name.LocalName == "Counters");
            foreach (var captured in report.Descendants().Where(element => element.Name.LocalName == "StdOut"))
                Console.WriteLine(captured.Value);
            Assert.AreEqual(0, child.ExitCode, report + Environment.NewLine + stdout + Environment.NewLine + stderr);
            Assert.AreEqual("1", counters.Attribute("total")?.Value);
            Assert.AreEqual("1", counters.Attribute("passed")?.Value);
        }
    }
}
