using System;
using IntraDeploy.Services;

namespace IntraDeploy.Models
{
    /// <summary>
    /// Everything the operator configured for one deployment run.
    /// Built by the UI, validated and executed by DeploymentService.
    /// </summary>
    public class DeploymentRequest
    {
        /// <summary>Application name, e.g. "BiCore". Also used for the deployment sub-folder and IIS defaults.</summary>
        public string ApplicationName { get; set; }

        /// <summary>Application version, e.g. "2.5.0". Used for the versioned deployment sub-folder.</summary>
        public string Version { get; set; }

        /// <summary>Source folder published by Visual Studio ("Publish as Folder").</summary>
        public string SourceFolder { get; set; }

        /// <summary>Root directory under which applications are deployed, e.g. C:\IntraDeploy\Applications.</summary>
        public string DeploymentRoot { get; set; }

        /// <summary>Computed target directory: DeploymentRoot\ApplicationName\Version.</summary>
        public string TargetFolder
        {
            get { return System.IO.Path.Combine(DeploymentRoot ?? string.Empty, ApplicationName ?? string.Empty, Version ?? string.Empty); }
        }

        /// <summary>IIS settings.</summary>
        public IisSettings Iis { get; set; }

        /// <summary>Database settings.</summary>
        public DatabaseSettings Database { get; set; }

        /// <summary>
        /// When true (default) IntraDeploy updates the first connection string in the deployed
        /// web.config / appsettings.json to point at the configured database.
        /// Connection string editing is opt-out because it changes published files.
        /// </summary>
        public bool UpdateConnectionString { get; set; }

        /// <summary>Connection string to write when UpdateConnectionString is true. Never logged.</summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Optional health-check path for this run (e.g. "/" or "/health").
        /// When null or whitespace, <see cref="Configuration.AppConfig.HealthCheckPath"/> is used.
        /// </summary>
        public string HealthCheckPath { get; set; }

        /// <summary>
        /// Optional post-deploy hooks for this run: semicolon-separated .ps1 paths and/or http(s) URLs.
        /// Null = use <see cref="Configuration.AppConfig.PostDeployHooks"/>; empty = no hooks.
        /// </summary>
        public string PostDeployHooks { get; set; }

        /// <summary>
        /// When true the operator explicitly confirmed overwriting an existing target
        /// deployment folder of the SAME version (see FileService). Set by the UI prompt.
        /// </summary>
        public bool AllowOverwriteExistingFolder { get; set; }

        /// <summary>
        /// When true, run pre-flight checks and report the planned actions, then exit
        /// before any file copy, IIS write, SQL restore, or config write.
        /// </summary>
        public bool DryRun { get; set; }

        /// <summary>Full or partial pipeline mode. Default is <see cref="DeploymentMode.Full"/>.</summary>
        public DeploymentMode Mode { get; set; }

        /// <summary>Detected application type, filled in by DeploymentService after probing.</summary>
        public ApplicationType DetectedApplicationType { get; set; }

        /// <summary>Validated backup plan (logical file names), filled in during the SQL check step.</summary>
        public RestorePlan RestorePlan { get; set; }

        /// <summary>Correlation id for this deployment run, used in log lines.</summary>
        public string RunId { get; set; }
    }
}
