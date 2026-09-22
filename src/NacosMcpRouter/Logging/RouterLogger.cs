using Microsoft.Extensions.Logging;

namespace NacosMcpRouter.Logging;

public static class RouterLogger
{
    public static ILoggerFactory CreateFactory() => LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
            o.UseUtcTimestamp = true;
        });
        builder.SetMinimumLevel(LogLevel.Information);
    });
}