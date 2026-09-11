using System.Collections.Generic;
using System.Linq;
using System.Text;
using IntraDeploy.Configuration;

namespace IntraDeploy.Models
{
    /// <summary>
    /// Which pipeline write steps a <see cref="DeploymentMode"/> runs, plus
    /// operator-facing step lists for confirm / dry-run.
    /// </summary>
    public static class DeploymentPlan
    {
        public static bool RunsCopy(DeploymentMode mode)
        {
            return mode == DeploymentMode.Full || mode == DeploymentMode.FilesAndIis;
        }

        public static bool RunsIisConfigure(DeploymentMode mode)
        {
            return mode == DeploymentMode.Full
                || mode == DeploymentMode.FilesAndIis
                || mode == DeploymentMode.IisOnly;
        }

        /// <summary>
        /// Database restore is attempted only when the mode includes DB and the request
        /// is in RestoreFromBak mode. UseExisting still means no SQL writes.
        /// </summary>
        public static bool RunsDatabase(DeploymentMode mode)
        {
            return mode == DeploymentMode.Full || mode == DeploymentMode.DatabaseOnly;
        }

        public static bool RunsApplyConfiguration(DeploymentMode mode)
        {
            return RunsCopy(mode);
        }

        public static bool RunsRecycle(DeploymentMode mode)
        {
            return mode == DeploymentMode.Full
                || mode == DeploymentMode.FilesAndIis
                || mode == DeploymentMode.IisOnly
                || mode == DeploymentMode.RecycleOnly;
        }

        public static bool RunsHealthCheck(DeploymentMode mode)
        {
            return RunsRecycle(mode);
        }

        public static bool NeedsSourceProbe(DeploymentMode mode)
        {
            return RunsCopy(mode) || RunsIisConfigure(mode);
        }

        public static bool NeedsIisAvailability(DeploymentMode mode)
        {
            return RunsIisConfigure(mode) || RunsRecycle(mode);
        }

        public static bool WillRestoreDatabase(DeploymentRequest request)
        {
            if (request == null || request.Database == null)
            {
                return false;
            }
            return RunsDatabase(request.Mode) && request.Database.Mode == DatabaseMode.RestoreFromBak;
        }

        public static string DescribeMode(DeploymentMode mode)
        {
            switch (mode)
            {
                case DeploymentMode.FilesAndIis:
                    return "Files + IIS (skip database)";
                case DeploymentMode.IisOnly:
                    return "IIS only";
                case DeploymentMode.DatabaseOnly:
                    return "Database only";
                case DeploymentMode.RecycleOnly:
                    return "Recycle only";
                default:
                    return "Full deploy";
            }
        }

        /// <summary>
        /// Ordered operator-facing lines of what will run (or dry-run plan only).
        /// </summary>
        public static List<string> ListOperatorSteps(DeploymentRequest request)
        {
            var steps = new List<string>();
            if (request == null)
            {
                return steps;
            }

            DeploymentMode mode = request.Mode;
            steps.Add("Mode: " + DescribeMode(mode));

            if (request.DryRun)
            {
                steps.Add("Pre-flight checks only, then report the plan");
                steps.Add("DRY-RUN: stop before copy / IIS writes / SQL restore / config writes");
                steps.Add("Planned target folder: " + request.TargetFolder);
                steps.Add("Planned IIS: configure pool/target when mode includes IIS");
                steps.Add("Planned database: " +
                          (WillRestoreDatabase(request) ? "RESTORE from backup" : "NONE"));
                steps.Add("Planned config: " +
                          (RunsApplyConfiguration(mode) && request.UpdateConnectionString
                              ? "Update connection string on deployed copy"
                              : "Leave config unchanged / skipped"));
                return steps;
            }

            steps.Add("Validate inputs");
            if (NeedsSourceProbe(mode))
            {
                steps.Add("Probe published application");
            }
            if (NeedsIisAvailability(mode))
            {
                steps.Add("Check IIS availability" +
                          (mode == DeploymentMode.IisOnly || RunsIisConfigure(mode)
                              ? " / Core hosting / port conflict"
                              : string.Empty));
            }
            if (WillRestoreDatabase(request))
            {
                steps.Add("Validate SQL Server and backup");
            }
            if (RunsCopy(mode))
            {
                steps.Add("Copy application files to " + request.TargetFolder);
            }
            if (RunsIisConfigure(mode))
            {
                steps.Add("Configure IIS application pool and site/app (create or update)");
            }
            if (WillRestoreDatabase(request))
            {
                steps.Add("Restore database from .bak");
            }
            else if (RunsDatabase(mode) && request.Database != null
                     && request.Database.Mode == DatabaseMode.UseExisting)
            {
                steps.Add("Database: none (use existing)");
            }
            else if (!RunsDatabase(mode))
            {
                steps.Add("Database: skipped (mode)");
            }
            if (RunsApplyConfiguration(mode))
            {
                steps.Add(request.UpdateConnectionString
                    ? "Update connection string in deployed config"
                    : "Apply configuration (no connection-string change)");
            }
            if (RunsRecycle(mode))
            {
                steps.Add("Start / recycle IIS");
            }
            if (RunsHealthCheck(mode))
            {
                steps.Add("Health check");
            }

            string hookRaw = request.PostDeployHooks != null
                ? request.PostDeployHooks
                : AppConfig.PostDeployHooks;
            int hookCount = CountHookEntries(hookRaw);
            if (hookCount > 0)
            {
                steps.Add("Post-deploy hooks (" + hookCount + ")");
            }

            return steps;
        }

        private static int CountHookEntries(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0;
            }
            return raw.Split(new[] { ';', ',' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Count(s => s.Trim().Length > 0);
        }

        /// <summary>
        /// Multi-line dry-run report after successful pre-flight (no secrets).
        /// </summary>
        public static string BuildDryRunSummary(DeploymentRequest request, string iisAction)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DRY-RUN completed — no files, IIS, SQL, or config were changed.");
            sb.AppendLine("Mode: " + DescribeMode(request != null ? request.Mode : DeploymentMode.Full));
            sb.AppendLine("Target folder: " + (request != null ? request.TargetFolder : "(none)"));
            sb.AppendLine("IIS: " + (string.IsNullOrWhiteSpace(iisAction) ? "(not evaluated)" : iisAction));
            if (WillRestoreDatabase(request))
            {
                sb.AppendLine("Database: RESTORE from backup (would run on a real deploy)");
            }
            else
            {
                sb.AppendLine("Database: NONE");
            }
            if (request != null && RunsApplyConfiguration(request.Mode) && request.UpdateConnectionString)
            {
                sb.AppendLine("Config: would update connection string on deployed copy");
            }
            else
            {
                sb.AppendLine("Config: none / skipped");
            }
            sb.AppendLine("Steps that would run:");
            if (request != null)
            {
                // Temporarily clear DryRun so ListOperatorSteps shows the real pipeline.
                bool wasDry = request.DryRun;
                request.DryRun = false;
                try
                {
                    foreach (string step in ListOperatorSteps(request))
                    {
                        if (step.StartsWith("Mode:", System.StringComparison.Ordinal))
                        {
                            continue;
                        }
                        sb.AppendLine("  - " + step);
                    }
                }
                finally
                {
                    request.DryRun = wasDry;
                }
            }
            return sb.ToString().TrimEnd();
        }
    }
}
