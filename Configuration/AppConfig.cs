using System;
using System.Configuration;

namespace IntraDeploy.Configuration
{
    /// <summary>
    /// Reads tool wide settings from App.config appSettings.
    /// Paths are not hard coded anywhere else in the application.
    /// </summary>
    public static class AppConfig
    {
        /// <summary>Root directory for deployed applications. Default C:\IntraDeploy\Applications.</summary>
        public static string DeploymentRoot
        {
            get { return GetSetting("DeploymentRoot", @"C:\IntraDeploy\Applications"); }
        }

        /// <summary>Default SQL Server used for connectivity checks. Default "localhost".</summary>
        public static string SqlServer
        {
            get { return GetSetting("SqlServer", "localhost"); }
        }

        /// <summary>Serilog rolling file directory. Default C:\IntraDeploy\Logs.</summary>
        public static string LogDirectory
        {
            get { return GetSetting("LogDirectory", @"C:\IntraDeploy\Logs"); }
        }

        /// <summary>Serilog minimum level. Default Information.</summary>
        public static string SerilogMinimumLevel
        {
            get { return GetSetting("SerilogMinimumLevel", "Information"); }
        }

        /// <summary>
        /// Relative path appended to the site/app base URL for the post-deploy health check.
        /// Default "/". UI may override per run via <see cref="Models.DeploymentRequest.HealthCheckPath"/>.
        /// </summary>
        public static string HealthCheckPath
        {
            get { return GetSetting("HealthCheckPath", "/"); }
        }

        /// <summary>HTTP timeout for the health check request, in seconds. Default 15. Clamped to 1–300.</summary>
        public static int HealthCheckTimeoutSeconds
        {
            get
            {
                int seconds = GetIntSetting("HealthCheckTimeoutSeconds", 15);
                if (seconds < 1)
                {
                    return 1;
                }
                if (seconds > 300)
                {
                    return 300;
                }
                return seconds;
            }
        }

        /// <summary>
        /// When true, a non-healthy health check marks the deployment as failed at HealthCheck.
        /// Default false (success with health warning) to preserve prior behavior.
        /// </summary>
        public static bool HealthCheckFailureFailsDeploy
        {
            get { return GetBoolSetting("HealthCheckFailureFailsDeploy", false); }
        }

        /// <summary>
        /// After a successful deploy, keep at most this many version folders under
        /// DeploymentRoot\App\. Oldest folders are pruned; the live IIS physical path
        /// is never deleted. Default 5. Values below 1 are treated as 1.
        /// </summary>
        public static int MaxVersionsToKeep
        {
            get
            {
                int value = GetIntSetting("MaxVersionsToKeep", 5);
                return value < 1 ? 1 : value;
            }
        }

        /// <summary>
        /// Semicolon-separated post-deploy hooks: local .ps1 paths and/or http(s) URLs.
        /// Empty = no hooks. Overridable per run via <see cref="Models.DeploymentRequest.PostDeployHooks"/>.
        /// </summary>
        public static string PostDeployHooks
        {
            get { return GetSetting("PostDeployHooks", string.Empty); }
        }

        /// <summary>
        /// When true, a failed post-deploy hook marks the deployment as failed at PostDeployHook.
        /// Default false (success with hook warning).
        /// </summary>
        public static bool PostDeployHookFailureFailsDeploy
        {
            get { return GetBoolSetting("PostDeployHookFailureFailsDeploy", false); }
        }

        /// <summary>Timeout for each post-deploy hook (script or HTTP), in seconds. Default 60. Clamped to 1–600.</summary>
        public static int PostDeployHookTimeoutSeconds
        {
            get
            {
                int seconds = GetIntSetting("PostDeployHookTimeoutSeconds", 60);
                if (seconds < 1)
                {
                    return 1;
                }
                if (seconds > 600)
                {
                    return 600;
                }
                return seconds;
            }
        }

        /// <summary>Parallel file-copy workers. Default 4. Clamped to 1–16.</summary>
        public static int CopyMaxDegreeOfParallelism
        {
            get
            {
                int value = GetIntSetting("CopyMaxDegreeOfParallelism", 4);
                if (value < 1)
                {
                    return 1;
                }
                if (value > 16)
                {
                    return 16;
                }
                return value;
            }
        }

        /// <summary>
        /// Overwrite compare mode. "SizeAndTime" (default) reuses unchanged files from .deploying-bak.
        /// Any other value falls back to always copy from source.
        /// </summary>
        public static string CopyCompareMode
        {
            get { return GetSetting("CopyCompareMode", "SizeAndTime"); }
        }

        /// <summary>Health check attempts (including the first). Default 3. Clamped to 1–10.</summary>
        public static int HealthCheckRetries
        {
            get
            {
                int value = GetIntSetting("HealthCheckRetries", 3);
                if (value < 1)
                {
                    return 1;
                }
                if (value > 10)
                {
                    return 10;
                }
                return value;
            }
        }

        /// <summary>Base delay between health retries in ms (multiplied by attempt). Default 2000. Clamped to 0–60000.</summary>
        public static int HealthCheckRetryDelayMilliseconds
        {
            get
            {
                int value = GetIntSetting("HealthCheckRetryDelayMilliseconds", 2000);
                if (value < 0)
                {
                    return 0;
                }
                if (value > 60000)
                {
                    return 60000;
                }
                return value;
            }
        }

        private static string GetSetting(string key, string fallback)
        {
            string value = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private static int GetIntSetting(string key, int fallback)
        {
            string value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            int parsed;
            return int.TryParse(value.Trim(), out parsed) ? parsed : fallback;
        }

        private static bool GetBoolSetting(string key, bool fallback)
        {
            string value = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }
            string trimmed = value.Trim();
            bool parsed;
            if (bool.TryParse(trimmed, out parsed))
            {
                return parsed;
            }
            if (string.Equals(trimmed, "1", StringComparison.Ordinal))
            {
                return true;
            }
            if (string.Equals(trimmed, "0", StringComparison.Ordinal))
            {
                return false;
            }
            return fallback;
        }
    }
}
