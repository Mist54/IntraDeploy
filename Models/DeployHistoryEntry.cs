using System;

namespace IntraDeploy.Models
{
    /// <summary>
    /// One append-only deploy/rollback history line. Never contains secrets
    /// (no connection strings, SQL passwords, or credentials).
    /// </summary>
    public class DeployHistoryEntry
    {
        /// <summary>Application name, e.g. BiCore.</summary>
        public string ApplicationName { get; set; }

        /// <summary>Version folder name, e.g. 2.5.0.</summary>
        public string Version { get; set; }

        /// <summary>Target deployment folder path.</summary>
        public string TargetFolder { get; set; }

        /// <summary>Health-check / site URL when known; otherwise null.</summary>
        public string Url { get; set; }

        /// <summary>True when the run completed successfully.</summary>
        public bool Success { get; set; }

        /// <summary>Failed pipeline step name, or null on success.</summary>
        public string FailedStep { get; set; }

        /// <summary>UTC timestamp when the entry was recorded.</summary>
        public DateTime TimestampUtc { get; set; }

        /// <summary>Hint to the Serilog daily file for this run (no secrets).</summary>
        public string LogHint { get; set; }

        /// <summary>IIS target description (site/app path or site+port), no secrets.</summary>
        public string IisTarget { get; set; }

        /// <summary>Optional operation kind: "Deploy", "DryRun", "Deploy:RecycleOnly", or "Rollback".</summary>
        public string Operation { get; set; }
    }
}
