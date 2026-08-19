using System;
using System.Net.Http;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace DvAppInsightsDemoPlugins
{
    public sealed class Snorlax : IPlugin
    {
        private static readonly Uri[] ApiEndpoints =
        {
            new Uri("https://httpbin.org/get"),
            new Uri("https://jsonplaceholder.typicode.com/todos/1"),
            new Uri("https://api.github.com/zen")
        };

        private static readonly HttpClient HttpClient = CreateHttpClient();
        private static readonly object RandomLock = new object();
        private static readonly Random Random = new Random();

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var logger = (ILogger)serviceProvider.GetService(typeof(ILogger));
            var callCount = Next(3, 7);

            tracingService.Trace("Snorlax: Starting a slow execution with {0} outbound API calls.", callCount);
            logger.LogInformation("Snorlax: Starting a slow execution with {0} outbound API calls.", callCount);

            for (var callNumber = 1; callNumber <= callCount; callNumber++)
            {
                var sleepMilliseconds = Next(1000, 3001);
                var endpoint = ApiEndpoints[Next(0, ApiEndpoints.Length)];

                tracingService.Trace(
                    "Snorlax: Sleeping for {0} ms before call {1} of {2} to {3}.",
                    sleepMilliseconds,
                    callNumber,
                    callCount,
                    endpoint);
                logger.LogInformation(
                    "Snorlax: Sleeping for {0} ms before call {1} of {2} to {3}.",
                    sleepMilliseconds,
                    callNumber,
                    callCount,
                    endpoint);
                Thread.Sleep(sleepMilliseconds);

                try
                {
                    using (var response = HttpClient.GetAsync(endpoint).GetAwaiter().GetResult())
                    {
                        tracingService.Trace(
                            "Snorlax: Call {0} of {1} completed with HTTP status {2}.",
                            callNumber,
                            callCount,
                            (int)response.StatusCode);
                        logger.LogInformation(
                            "Snorlax: Call {0} of {1} completed with HTTP status {2}.",
                            callNumber,
                            callCount,
                            (int)response.StatusCode);
                    }
                }
                catch (Exception exception)
                {
                    tracingService.Trace(
                        "Snorlax: Call {0} of {1} failed with {2}: {3}",
                        callNumber,
                        callCount,
                        exception.GetType().Name,
                        exception.Message);
                    logger.LogError(
                        exception,
                        "Snorlax: Call {0} of {1} failed with {2}: {3}",
                        callNumber,
                        callCount,
                        exception.GetType().Name,
                        exception.Message);
                }
            }

            tracingService.Trace("Snorlax: Slow execution completed.");
            logger.LogInformation("Snorlax: Slow execution completed.");
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DvAppInsightsDemoPlugins/1.0");
            return client;
        }

        private static int Next(int minimumValue, int maximumValue)
        {
            lock (RandomLock)
            {
                return Random.Next(minimumValue, maximumValue);
            }
        }
    }
}
