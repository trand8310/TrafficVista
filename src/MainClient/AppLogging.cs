using Serilog;
using Serilog.Debugging;
using System.Diagnostics;
using System.Text;

namespace TrafficVista;

internal static class AppLogging
{
    internal static readonly UiLogging.LiveLogBuffer LiveLogs = new();
    private static readonly Serilog.Core.LoggingLevelSwitch LevelSwitch = new(Serilog.Events.LogEventLevel.Information);
    public static void SetVerbose(bool enabled) => LevelSwitch.MinimumLevel = enabled
        ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information;
    private static bool _testMode;
    public static string LogDirectory => _testMode ? Path.Combine(AppContext.BaseDirectory, "demo-output", "logs")
        : Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Configure(bool testMode = false)
    {
        _testMode = testMode;
        Directory.CreateDirectory(LogDirectory);
        SelfLog.Enable(message => Trace.WriteLine(message));
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.WithProperty("Application", "TrafficVista")
            .Enrich.WithProperty("ProcessId", Environment.ProcessId)
            .Enrich.WithProperty("RunId", Guid.NewGuid().ToString("N"))
            .Enrich.WithProperty("RunMode", testMode ? "self-test" : "interactive")
            .WriteTo.Sink(new UiLogging.LiveLogSink(LiveLogs))
            .WriteTo.File(
                Path.Combine(LogDirectory, "trafficvista-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                buffered: false,
                shared: true,
                encoding: new UTF8Encoding(false),
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [PID:{ProcessId}] [Run:{RunId}] [{RunMode}] [{SourceContext}] [Instance:{InstanceId}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
