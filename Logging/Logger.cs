using System;
using System.IO;
using IntraDeploy.Configuration;
using Serilog;
using Serilog.Events;

namespace IntraDeploy.Logging
{
    /// <summary>
    /// Configures the application wide Serilog logger.
    /// Logs go to the configured LogDirectory (default C:\IntraDeploy\Logs) as rolling daily files.
    /// </summary>
    public static class Logger
    {
        private static bool _initialized;

        /// <summary>Initializes Serilog once. Safe to call multiple times.</summary>
        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            string directory = AppConfig.LogDirectory;
            string filePath = Path.Combine(directory, "intradeploy-.log");

            LogEventLevel minimumLevel = ParseLevel(AppConfig.SerilogMinimumLevel);

            var configuration = new LoggerConfiguration()
                .MinimumLevel.Is(minimumLevel)
                .WriteTo.File(
                    filePath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 60,
                    shared: false,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .Enrich.FromLogContext();

            Log.Logger = configuration.CreateLogger();
            _initialized = true;

            Log.Information("IntraDeploy logging initialized. Log directory: {LogDirectory}", directory);
        }

        /// <summary>Flushes and disposes the Serilog logger. Call on application exit.</summary>
        public static void Shutdown()
        {
            Log.CloseAndFlush();
        }

        private static LogEventLevel ParseLevel(string value)
        {
            LogEventLevel level;
            if (Enum.TryParse(value, true, out level))
            {
                return level;
            }
            return LogEventLevel.Information;
        }
    }
}
