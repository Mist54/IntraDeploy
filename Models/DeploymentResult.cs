namespace IntraDeploy.Models
{
    /// <summary>Outcome of the post-deployment health check.</summary>
    public enum HealthCheckOutcome
    {
        /// <summary>Health check has not run (deployment failed earlier).</summary>
        NotRun = 0,

        /// <summary>The endpoint answered with a healthy status (2xx/3xx).</summary>
        Healthy = 1,

        /// <summary>The endpoint answered but reported an error status (4xx/5xx).</summary>
        UnhealthyResponse = 2,

        /// <summary>The endpoint could not be reached at all.</summary>
        Unreachable = 3
    }

    /// <summary>
    /// Result of a deployment run.
    /// </summary>
    public class DeploymentResult
    {
        public DeploymentResult()
        {
            RunId = string.Empty;
        }

        /// <summary>True when every critical step succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Correlation id from the request.</summary>
        public string RunId { get; set; }

        /// <summary>Failed pipeline step, or Completed on success.</summary>
        public DeploymentStep FailedAt { get; set; }

        /// <summary>Operator facing error message, or null on success.</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Outcome of the health check.</summary>
        public HealthCheckOutcome Health { get; set; }

        /// <summary>Final health check summary (URL, status code, description).</summary>
        public string HealthCheckSummary { get; set; }

        /// <summary>Target folder that received the application files.</summary>
        public string TargetFolder { get; set; }

        /// <summary>URL that was checked, when the health check ran.</summary>
        public string HealthCheckUrl { get; set; }

        /// <summary>
        /// Note about rollback / restore of the previous version when an overwrite copy
        /// failed (e.g. "Previous deployment restored."). Null when nothing was rolled back.
        /// </summary>
        public string RollbackNote { get; set; }

        /// <summary>
        /// When the request was a dry-run, the planned target / IIS / DB / config summary.
        /// Null for normal deploys.
        /// </summary>
        public string DryRunSummary { get; set; }

        /// <summary>
        /// Combined summary of post-deploy hook runs (scripts / HTTP). Null when no hooks ran.
        /// </summary>
        public string PostDeployHookSummary { get; set; }

        /// <summary>
        /// Aggregate exit code from post-deploy hooks (0 = all ok). Null when no hooks ran.
        /// </summary>
        public int? PostDeployHookExitCode { get; set; }
    }
}
