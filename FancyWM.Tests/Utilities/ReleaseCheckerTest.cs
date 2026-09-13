#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FancyWM.Utilities;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FancyWM.Tests.Utilities
{
    [TestClass]
    public class ReleaseCheckerTest
    {
        private const string Releases = "[{\"tag_name\":\"v9.0.0-rc1\",\"prerelease\":false},{\"tag_name\":\"v2.0.0\",\"prerelease\":true},{\"tag_name\":\"v1.2.3\",\"prerelease\":false}]";

        [TestMethod]
        public async Task FactoryCreatedSingletonClosesItsClientAcrossOneHundredProviderLifetimes()
        {
            int closed = 0;
            for (int cycle = 0; cycle < 100; cycle++)
            {
                var handler = new CountingHandler((_, _) => Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases) }));
                using var client = new HttpClient(handler);
                using var provider = CreateProvider(client);
                var checker = provider.GetRequiredService<ReleaseChecker>();
                Assert.AreSame(checker, provider.GetRequiredService<ReleaseChecker>());
                Assert.AreEqual(new Version(1, 2, 3), await checker.GetLatestStableVersionAsync("owner", "repo"));
                provider.Dispose();
                provider.Dispose();
                Assert.AreEqual(1, handler.DisposeCount,
                    "The factory singleton must participate in the existing DI owner's disposal.");
                checker.Dispose();
                Assert.AreEqual(1, handler.DisposeCount);
                Assert.AreEqual(1, handler.SendCount);
                closed += handler.DisposeCount;
            }
            Assert.AreEqual(100, closed);
            Console.WriteLine($"PERFCOUNTER release-checker disposed-handlers {closed}");
            Console.WriteLine("PERFCOUNTER release-checker cycles 100");
        }

        [TestMethod]
        public async Task ProviderDisposalCancelsTheOwnedPendingRequest()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new CountingHandler(async (_, token) =>
            {
                using var registration = token.Register(() =>
                {
                    canceled.TrySetResult();
                    response.TrySetCanceled(token);
                });
                entered.TrySetResult();
                return await response.Task.ConfigureAwait(false);
            });
            using var client = new HttpClient(handler);
            using var provider = CreateProvider(client);
            var checker = provider.GetRequiredService<ReleaseChecker>();
            var pending = checker.GetLatestStableVersionAsync("owner", "repo");
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(pending.IsCompleted);
                provider.Dispose();
                Assert.IsTrue(canceled.Task.IsCompleted,
                    "Provider disposal must reach HttpClient cancellation without waiting for response bytes.");
                await AssertCanceled(pending);
                Assert.AreEqual(1, handler.DisposeCount);
            }
            finally
            {
                // Clean up even when this test is intentionally red before the
                // IDisposable interface is added; no pending transport is left.
                client.Dispose();
                await AssertCanceled(pending);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task FailedRequestReleasesResponseAndProviderOwner(bool malformedJson)
        {
            var content = new CountingContent(malformedJson ? "not-json" : "unavailable");
            var handler = new CountingHandler((_, _) => Task.FromResult(new HttpResponseMessage(
                malformedJson ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable) { Content = content }));
            using var client = new HttpClient(handler);
            using var provider = CreateProvider(client);
            var checker = provider.GetRequiredService<ReleaseChecker>();
            if (malformedJson)
            {
                await Assert.ThrowsExceptionAsync<JsonException>(() => checker.GetLatestStableVersionAsync("owner", "repo"));
            }
            else
            {
                await Assert.ThrowsExceptionAsync<HttpRequestException>(() => checker.GetLatestStableVersionAsync("owner", "repo"));
            }
            Assert.AreEqual(1, content.DisposeCount);
            provider.Dispose();
            Assert.AreEqual(1, handler.DisposeCount);
        }

        [TestMethod]
        public async Task RetainedServiceReferenceCannotSendAfterProviderDisposal()
        {
            var handler = new CountingHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases) }));
            using var client = new HttpClient(handler);
            using var provider = CreateProvider(client);
            var checker = provider.GetRequiredService<ReleaseChecker>();
            provider.Dispose();
            await Assert.ThrowsExceptionAsync<ObjectDisposedException>(() => checker.GetLatestStableVersionAsync("owner", "repo"));
            Assert.AreEqual(0, handler.SendCount);
            Assert.AreEqual(1, handler.DisposeCount);
        }

        private static ServiceProvider CreateProvider(HttpClient client)
        {
            var services = new ServiceCollection();
            services.AddSingleton(_ => new ReleaseChecker(client));
            return services.BuildServiceProvider();
        }

        private static async Task AssertCanceled(Task task)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail("The pending request unexpectedly completed successfully.");
            }
            catch (OperationCanceledException) { }
        }

        private sealed class CountingHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            public int SendCount { get; private set; }
            public int DisposeCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                SendCount++;
                return send(request, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { DisposeCount++; }
                base.Dispose(disposing);
            }
        }

        private sealed class CountingContent(string content) : StringContent(content)
        {
            public int DisposeCount { get; private set; }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { DisposeCount++; }
                base.Dispose(disposing);
            }
        }
    }
}
