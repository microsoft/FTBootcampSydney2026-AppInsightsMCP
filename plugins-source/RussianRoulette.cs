using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.PluginTelemetry;

namespace DvAppInsightsDemoPlugins
{
    public sealed class RussianRoulette : IPlugin
    {
        private static readonly object RandomLock = new object();
        private static readonly Random Random = new Random();

        public void Execute(IServiceProvider serviceProvider)
        {
            var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            ILogger logger = (ILogger)serviceProvider.GetService(typeof(ILogger));
            tracingService.Trace("RussianRoulette: Spinning the barrel.");
            logger.LogInformation("RussianRoulette: Spinning the barrel.");

            int chamber;
            lock (RandomLock)
            {
                chamber = Random.Next(6);
            }

            if (chamber == 0)
            {
                tracingService.Trace("RussianRoulette: Bang!");
                logger.LogError("RussianRoulette: Bang!");
                try
                {
                    RecursiveMethodWithNoLimit(tracingService);

                }
                catch (Exception ex)
                {
                    tracingService.Trace($"RussianRoulette: Caught an exception: {ex.Message}");
                    logger.LogError($"RussianRoulette: Caught an exception: {ex.Message}");
                    throw new InvalidPluginExecutionException("You will never see this message.");
                }
            }
            if (chamber == 6)
            {
                tracingService.Trace("RussianRoulette: Bang!");
                logger.LogError("RussianRoulette: Bang!");
                try
                {
                    bool flag = true;
                    IConvertible conv = flag;
                    Char ch = conv.ToChar(null);
                }
                catch (Exception ex)
                {
                    tracingService.Trace($"RussianRoulette: Caught an exception: {ex.Message}");
                    logger.LogError($"RussianRoulette: Caught an exception: {ex.Message}");
                    throw new InvalidPluginExecutionException("An error occurred in RussianRoulette. You lost this time :(");
                }
            }


            tracingService.Trace("RussianRoulette: Click. This execution survived.");
            logger.LogInformation("RussianRoulette: Click. This execution survived.");
        }

        public static void RecursiveMethodWithNoLimit(ITracingService svc)
        {
            RecursiveMethodWithNoLimit(svc);
        }
    }
}
