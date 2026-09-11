using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using IntraDeploy.Configuration;
using IntraDeploy.Models;

namespace IntraDeploy.Utilities
{
    /// <summary>
    /// Append-only JSONL deploy history under %LOCALAPPDATA%\IntraDeploy\history.jsonl.
    /// Best effort only: missing/corrupt lines are skipped; never fails the app.
    /// Never stores secrets.
    /// </summary>
    public static class DeployHistoryStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IntraDeploy",
            "history.jsonl");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>Absolute path of the history file (for diagnostics / docs).</summary>
        public static string HistoryFilePath
        {
            get { return StorePath; }
        }

        /// <summary>Appends one history entry. Best effort; never throws to the caller.</summary>
        public static void Append(DeployHistoryEntry entry)
        {
            Append(entry, StorePath);
        }

        /// <summary>Appends to a specific JSONL path (used by SmokeTest).</summary>
        public static void Append(DeployHistoryEntry entry, string storePath)
        {
            if (entry == null || string.IsNullOrWhiteSpace(storePath))
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(storePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (entry.TimestampUtc == default(DateTime))
                {
                    entry.TimestampUtc = DateTime.UtcNow;
                }

                string line = JsonSerializer.Serialize(entry, JsonOptions);
                File.AppendAllText(storePath, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Convenience history only; never fail a deploy over it.
            }
        }

        /// <summary>
        /// Reads recent entries newest-first. Corrupt lines are skipped.
        /// </summary>
        public static IList<DeployHistoryEntry> ReadRecent(int maxCount)
        {
            return ReadRecent(maxCount, StorePath);
        }

        /// <summary>Reads from a specific JSONL path (used by SmokeTest).</summary>
        public static IList<DeployHistoryEntry> ReadRecent(int maxCount, string storePath)
        {
            var results = new List<DeployHistoryEntry>();
            if (maxCount <= 0 || string.IsNullOrWhiteSpace(storePath))
            {
                return results;
            }

            try
            {
                if (!File.Exists(storePath))
                {
                    return results;
                }

                string[] lines = File.ReadAllLines(storePath);
                for (int i = lines.Length - 1; i >= 0 && results.Count < maxCount; i--)
                {
                    string line = lines[i];
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    try
                    {
                        DeployHistoryEntry entry = JsonSerializer.Deserialize<DeployHistoryEntry>(line, JsonOptions);
                        if (entry != null)
                        {
                            results.Add(entry);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip corrupt line.
                    }
                }
            }
            catch (Exception)
            {
                // Convenience history only.
            }

            return results;
        }

        /// <summary>
        /// Builds a log-file hint for today under the configured LogDirectory
        /// (e.g. C:\IntraDeploy\Logs\intradeploy-20260911.log).
        /// </summary>
        public static string BuildLogHint()
        {
            try
            {
                string fileName = "intradeploy-" + DateTime.Now.ToString("yyyyMMdd") + ".log";
                return Path.Combine(AppConfig.LogDirectory, fileName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Creates a history entry from a completed deploy run (no secrets).</summary>
        public static DeployHistoryEntry FromDeploy(DeploymentRequest request, DeploymentResult result)
        {
            if (request == null || result == null)
            {
                return null;
            }

            string failedStep = null;
            if (!result.Success && result.FailedAt != DeploymentStep.NotStarted)
            {
                failedStep = result.FailedAt.ToString();
            }
            else if (!result.Success)
            {
                failedStep = "Cancelled";
            }

            string operation = "Deploy";
            if (request.DryRun)
            {
                operation = "DryRun";
            }
            else if (request.Mode != DeploymentMode.Full)
            {
                operation = "Deploy:" + request.Mode;
            }

            return new DeployHistoryEntry
            {
                ApplicationName = request.ApplicationName,
                Version = request.Version,
                TargetFolder = result.TargetFolder ?? request.TargetFolder,
                Url = result.HealthCheckUrl,
                Success = result.Success,
                FailedStep = failedStep,
                TimestampUtc = DateTime.UtcNow,
                LogHint = BuildLogHint(),
                IisTarget = DescribeIisTarget(request),
                Operation = operation
            };
        }

        /// <summary>Creates a history entry for an IIS path rollback (no secrets).</summary>
        public static DeployHistoryEntry FromRollback(
            DeploymentRequest request,
            string versionFolder,
            string physicalPath,
            string url,
            bool success,
            string errorMessage)
        {
            return new DeployHistoryEntry
            {
                ApplicationName = request != null ? request.ApplicationName : null,
                Version = versionFolder,
                TargetFolder = physicalPath,
                Url = url,
                Success = success,
                FailedStep = success ? null : "Rollback",
                TimestampUtc = DateTime.UtcNow,
                LogHint = BuildLogHint(),
                IisTarget = DescribeIisTarget(request),
                Operation = "Rollback"
            };
        }

        private static string DescribeIisTarget(DeploymentRequest request)
        {
            if (request == null || request.Iis == null)
            {
                return null;
            }

            if (request.Iis.Mode == IisMode.ApplicationUnderSite)
            {
                return request.Iis.ParentSiteName + " → " + request.Iis.NormalizedApplicationPath;
            }

            return request.Iis.SiteName + " (port " + request.Iis.Port + ")";
        }
    }
}
