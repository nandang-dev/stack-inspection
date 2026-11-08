using Microsoft.Extensions.Logging;
using StackInspection.Application.Abstractions;
using StackInspection.Infrastructure.Logging;

namespace StackInspection.Infrastructure.Tests;

public partial class ProcessingLogCollectorTests
{
    [Fact]
    public async Task ParallelCaptures_KeepTheirOwnEntries_AndNothingIsCapturedOutside()
    {
        ProcessingLogCollector collector = new();
        ILogger logger = collector.CreateLogger("StackInspection.Application.CollectSku.CollectSkuHandler");
        LogOutside(logger);

        async Task<IReadOnlyList<ProcessingLogEntry>> Run(string name)
        {
            using IProcessingLogCapture capture = collector.Begin();
            for (int i = 0; i < 3; i++)
            {
                await Task.Yield();
                LogStep(logger, name, i);
            }

            LogDetail(logger);
            return capture.Entries;
        }

        IReadOnlyList<ProcessingLogEntry>[] results = await Task.WhenAll(Run("A"), Run("B"));

        Assert.Equal(["A langkah 0", "A langkah 1", "A langkah 2"], results[0].Select(e => e.Message));
        Assert.Equal(["B langkah 0", "B langkah 1", "B langkah 2"], results[1].Select(e => e.Message));
        Assert.All(results.SelectMany(r => r), e => Assert.Equal(("Information", "CollectSkuHandler"), (e.Level, e.Source)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "di luar penangkapan")]
    private static partial void LogOutside(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} langkah {Step}")]
    private static partial void LogStep(ILogger logger, string name, int step);

    [LoggerMessage(Level = LogLevel.Debug, Message = "debug tidak ditangkap")]
    private static partial void LogDetail(ILogger logger);
}
