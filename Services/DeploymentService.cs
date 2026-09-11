using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using IntraDeploy.Configuration;
using IntraDeploy.Models;
using IntraDeploy.Utilities;

namespace IntraDeploy.Services
{
    /// <summary>
    /// Orchestrates the deployment pipeline. The UI only collects input and shows progress;
    /// all real work lives in the services. Any step failure stops the pipeline, reports the
    /// exact failed step, and never reports success.
    /// Supports dry-run (plan only, no writes) and partial <see cref="DeploymentMode"/>s.
    /// </summary>
    public class DeploymentService
    {
        private readonly ApplicationDetector _applicationDetector;
        private readonly FileService _fileService;
        private readonly IisService _iisService;
        private readonly DatabaseService _databaseService;
        private readonly ConfigurationService _configurationService;

        public DeploymentService(
            ApplicationDetector applicationDetector,
            FileService fileService,
            IisService iisService,
            DatabaseService databaseService,
            ConfigurationService configurationService)
        {
            _applicationDetector = applicationDetector;
            _fileService = fileService;
            _iisService = iisService;
            _databaseService = databaseService;
            _configurationService = configurationService;
        }

        /// <summary>Raised for each pipeline step; the UI marshals to its own thread.</summary>
        public event EventHandler<ProgressEventArgs> Progress;

        /// <summary>
        /// Runs the deployment pipeline (full, partial, or dry-run). Never throws for
        /// deployment failures; failures come back as a failed DeploymentResult.
        /// </summary>
        public Task<DeploymentResult> DeployAsync(DeploymentRequest request, CancellationToken token)
        {
            return Task.Run(() => Deploy(request, token), token);
        }

        private DeploymentResult Deploy(DeploymentRequest request, CancellationToken token)
        {
            var result = new DeploymentResult
            {
                RunId = request.RunId,
                TargetFolder = request.TargetFolder,
                Health = HealthCheckOutcome.NotRun
            };

            using (Serilog.Context.LogContext.PushProperty("RunId", request.RunId))
            {
                Log.Information("=== Deployment started === App: {App} v{Version}, source: {Source}, " +
                                "mode: {DeployMode}, dryRun: {DryRun}, IIS mode: {IisMode}, site: {Site}, " +
                                "parent: {Parent}, app path: {AppPath}, db mode: {DbMode}",
                    request.ApplicationName, request.Version, request.SourceFolder,
                    request.Mode, request.DryRun,
                    request.Iis.Mode, request.Iis.SiteName, request.Iis.ParentSiteName,
                    request.Iis.NormalizedApplicationPath, request.Database.Mode);

                try
                {
                    DeploymentMode mode = request.Mode;
                    ApplicationProbeResult probe = null;

                    // 1. Validate inputs ------------------------------------------------
                    Report(DeploymentStep.ValidateInputs, "Validating inputs...", 0, false);
                    ValidateInputs(request);
                    token.ThrowIfCancellationRequested();

                    // 2. Probe when files or IIS configure need app type -----------------
                    if (DeploymentPlan.NeedsSourceProbe(mode))
                    {
                        Report(DeploymentStep.ProbeApplication, "Analyzing published application...", 5, false);
                        probe = _applicationDetector.Probe(request.SourceFolder);
                        if (!probe.IsValid)
                        {
                            throw new DeploymentStepException(DeploymentStep.ProbeApplication, probe.ErrorMessage);
                        }
                        request.DetectedApplicationType = probe.Info.Type;
                        Log.Information("Detected application type: {Type} ({Description})",
                            probe.Info.Type, probe.Info.Description);
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log.Information("Skipping application probe for mode {Mode}.", mode);
                    }

                    // 3. IIS availability when IIS configure or recycle will run --------
                    if (DeploymentPlan.NeedsIisAvailability(mode))
                    {
                        Report(DeploymentStep.CheckIisAvailability, "Checking IIS availability...", 10, false);
                        if (!_iisService.IsIisAvailable())
                        {
                            throw new DeploymentStepException(DeploymentStep.CheckIisAvailability,
                                "IIS is not installed or cannot be accessed. Install IIS with application support " +
                                "and run IntraDeploy as administrator.");
                        }

                        if (DeploymentPlan.RunsIisConfigure(mode) && probe != null)
                        {
                            string prerequisiteWarning = _applicationDetector.GetPrerequisiteWarning(probe.Info);
                            if (prerequisiteWarning != null)
                            {
                                throw new DeploymentStepException(DeploymentStep.CheckIisAvailability,
                                    "ASP.NET Core Hosting Bundle / AspNetCoreModuleV2 was not detected. " +
                                    "The application cannot be hosted through IIS until the required IIS module is installed. " +
                                    "Deployment was stopped before any IIS configuration was changed. " + prerequisiteWarning);
                            }

                            string portConflict = _iisService.GetPortConflict(request);
                            if (portConflict != null)
                            {
                                throw new DeploymentStepException(DeploymentStep.CheckIisAvailability, portConflict);
                            }
                        }
                        token.ThrowIfCancellationRequested();
                    }

                    // 4. SQL Server only when a restore would run -----------------------
                    if (DeploymentPlan.WillRestoreDatabase(request))
                    {
                        Report(DeploymentStep.CheckSqlServer, "Validating SQL Server connection and backup file...", 15, false);
                        _databaseService.TestConnection(request.Database);
                        request.RestorePlan = _databaseService.ValidateBackup(request.Database);
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log.Information("No database restore for this run (mode={Mode}, db={DbMode}).",
                            mode, request.Database != null ? request.Database.Mode.ToString() : "null");
                    }

                    // Dry-run: report plan and exit before any writes -------------------
                    if (request.DryRun)
                    {
                        string iisAction = "(IIS not required for this mode)";
                        if (DeploymentPlan.NeedsIisAvailability(mode))
                        {
                            try
                            {
                                IisLiveState live = _iisService.GetLiveState(request);
                                iisAction = live != null && !string.IsNullOrWhiteSpace(live.Action)
                                    ? live.Action
                                    : "(IIS state unavailable)";
                                if (live != null && !string.IsNullOrWhiteSpace(live.Details))
                                {
                                    iisAction = iisAction + " — " + live.Details;
                                }
                            }
                            catch (Exception ex)
                            {
                                Log.Warning(ex, "Dry-run could not read IIS live state.");
                                iisAction = "(IIS state unavailable: " + ex.Message + ")";
                            }
                        }

                        result.DryRunSummary = DeploymentPlan.BuildDryRunSummary(request, iisAction);
                        string hookList = PostDeployHookRunner.ResolveHookList(request);
                        string[] plannedHooks = PostDeployHookRunner.ParseHookList(hookList);
                        if (plannedHooks.Length > 0)
                        {
                            result.DryRunSummary += Environment.NewLine + "Post-deploy hooks (would run after health): "
                                + string.Join("; ", plannedHooks);
                        }
                        result.Success = true;
                        result.FailedAt = DeploymentStep.Completed;
                        Report(DeploymentStep.Completed, "Dry-run completed. No changes were made.", 100, false);
                        Log.Information("=== Dry-run completed === App: {App} v{Version}, target: {Target}",
                            request.ApplicationName, request.Version, request.TargetFolder);
                        DeployHistoryStore.Append(DeployHistoryStore.FromDeploy(request, result));
                        return result;
                    }

                    // 4b. Stop the running application when overwriting its live folder ----
                    bool isOverwrite = Directory.Exists(request.TargetFolder);
                    if (DeploymentPlan.RunsCopy(mode) && isOverwrite && request.AllowOverwriteExistingFolder)
                    {
                        Report(DeploymentStep.PrepareTargetFolder, "Stopping the running application...", 18, false);
                        _iisService.StopTargetApplication(request);
                    }

                    // 5. Copy files -------------------------------------------------------
                    if (DeploymentPlan.RunsCopy(mode))
                    {
                        Report(DeploymentStep.PrepareTargetFolder, "Preparing deployment folder...", 20, false);
                        Report(DeploymentStep.CopyApplicationFiles, "Copying application files...", 25, true);
                        _fileService.CopyApplication(request, args => Report(args.Step, args.Message, args.Percent, args.IsIndeterminate), token);
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log.Information("Skipping file copy for mode {Mode}.", mode);
                    }

                    // 6. IIS configuration --------------------------------------------------
                    if (DeploymentPlan.RunsIisConfigure(mode))
                    {
                        Report(DeploymentStep.ConfigureIis, "Configuring IIS application pool and target...", 60, false);
                        ApplicationType appType = probe != null ? probe.Info.Type : request.DetectedApplicationType;
                        _iisService.EnsureApplicationPool(request, appType);
                        _iisService.EnsureTarget(request);
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log.Information("Skipping IIS configure for mode {Mode}.", mode);
                    }

                    // 7. Database operation ---------------------------------------------------
                    if (DeploymentPlan.WillRestoreDatabase(request))
                    {
                        Report(DeploymentStep.DatabaseOperation, "Restoring database...", 70, true);
                        _databaseService.RestoreBackup(request.Database, request.RestorePlan);
                        token.ThrowIfCancellationRequested();
                    }

                    // 8. Configuration ------------------------------------------------------------
                    if (DeploymentPlan.RunsApplyConfiguration(mode))
                    {
                        Report(DeploymentStep.ApplyConfiguration, "Applying configuration...", 80, false);
                        ApplicationInfo info = probe != null
                            ? probe.Info
                            : new ApplicationInfo { Type = request.DetectedApplicationType };
                        string configSummary = _configurationService.ApplyConfiguration(request, info);
                        Log.Information("Configuration step: {Summary}", configSummary);
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log.Information("Skipping apply configuration for mode {Mode}.", mode);
                    }

                    // 9. Recycle / start ---------------------------------------------------------
                    if (DeploymentPlan.RunsRecycle(mode))
                    {
                        Report(DeploymentStep.StartAndRecycle, "Starting / recycling IIS...", 90, false);
                        _iisService.RecycleAndStart(request);
                        token.ThrowIfCancellationRequested();
                    }
                    else
                    {
                        Log.Information("Skipping recycle for mode {Mode}.", mode);
                    }

                    // 10. Health check ------------------------------------------------------------
                    bool pipelineSucceeded = false;
                    if (DeploymentPlan.RunsHealthCheck(mode))
                    {
                        Report(DeploymentStep.HealthCheck, "Running health check...", 95, false);
                        string baseUrl = _iisService.BuildUrl(request);
                        string healthPath = ResolveHealthCheckPath(request);
                        string url = CombineHealthCheckUrl(baseUrl, healthPath);
                        result.HealthCheckUrl = url;
                        int timeoutMs = AppConfig.HealthCheckTimeoutSeconds * 1000;
                        var health = PerformHealthCheckWithRetries(url, timeoutMs);
                        result.Health = health.Outcome;
                        result.HealthCheckSummary = health.Summary;
                        token.ThrowIfCancellationRequested();

                        if (health.Outcome != HealthCheckOutcome.Healthy
                            && AppConfig.HealthCheckFailureFailsDeploy)
                        {
                            result.Success = false;
                            result.FailedAt = DeploymentStep.HealthCheck;
                            result.ErrorMessage = "Health check failed: " + health.Summary;
                            Report(DeploymentStep.HealthCheck, "FAILED: " + result.ErrorMessage, 100, false);
                            Log.Error(
                                "=== Deployment failed at HealthCheck === App: {App} v{Version}, health: {Health}, url: {Url}",
                                request.ApplicationName, request.Version, health.Outcome, url);
                        }
                        else
                        {
                            pipelineSucceeded = true;
                            if (DeploymentPlan.RunsCopy(mode))
                            {
                                TryPruneAfterSuccess(request);
                            }

                            if (!TryRunPostDeployHooks(request, result, token))
                            {
                                // FailedAt / Success already set by hook runner policy.
                            }
                            else
                            {
                                Report(DeploymentStep.Completed,
                                    health.Outcome == HealthCheckOutcome.Healthy
                                        ? "Deployment completed. Health check passed."
                                        : "Deployment completed, but the health check failed.",
                                    100, false);
                                result.Success = true;
                                Log.Information(
                                    "=== Deployment completed (health: {Health}) === App: {App} v{Version}, target: {Target}",
                                    health.Outcome, request.ApplicationName, request.Version, request.TargetFolder);
                            }
                        }
                    }
                    else
                    {
                        pipelineSucceeded = true;
                        if (DeploymentPlan.RunsCopy(mode))
                        {
                            TryPruneAfterSuccess(request);
                        }

                        if (!TryRunPostDeployHooks(request, result, token))
                        {
                            // FailedAt / Success already set.
                        }
                        else
                        {
                            Report(DeploymentStep.Completed, "Deployment completed (health check skipped for mode).", 100, false);
                            result.Success = true;
                            Log.Information(
                                "=== Deployment completed (no health check) === App: {App} v{Version}, mode: {Mode}",
                                request.ApplicationName, request.Version, mode);
                        }
                    }

                    if (pipelineSucceeded && result.Success && result.PostDeployHookExitCode != null
                        && result.PostDeployHookExitCode.Value != 0)
                    {
                        Log.Warning("Deployment succeeded with post-deploy hook warning (exit {Exit})",
                            result.PostDeployHookExitCode);
                    }
                }
                catch (OperationCanceledException)
                {
                    result.Success = false;
                    result.FailedAt = DeploymentStep.NotStarted;
                    result.ErrorMessage = "Deployment cancelled by operator.";
                    Log.Warning("=== Deployment cancelled by operator === App: {App}", request.ApplicationName);
                }
                catch (DeploymentStepException ex)
                {
                    result.Success = false;
                    result.FailedAt = ex.Step;
                    result.ErrorMessage = ex.Message;
                    result.RollbackNote = ex.RollbackNote;
                    Report(ex.Step, "FAILED: " + ex.Message, 100, false);
                    Log.Error(ex, "=== Deployment failed at step {Step} === App: {App}", ex.Step, request.ApplicationName);
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.FailedAt = DeploymentStep.NotStarted;
                    result.ErrorMessage = "Unexpected error: " + ex.Message;
                    Log.Error(ex, "=== Deployment failed with unexpected error === App: {App}", request.ApplicationName);
                }

                DeployHistoryStore.Append(DeployHistoryStore.FromDeploy(request, result));
            }

            return result;
        }

        private void TryPruneAfterSuccess(DeploymentRequest request)
        {
            try
            {
                _fileService.PruneOldVersions(
                    request.DeploymentRoot,
                    request.ApplicationName,
                    AppConfig.MaxVersionsToKeep,
                    request.TargetFolder);
            }
            catch (Exception pruneEx)
            {
                Log.Warning(pruneEx, "Version prune failed after successful deploy for {App}",
                    request.ApplicationName);
            }
        }

        /// <summary>
        /// Runs configured post-deploy hooks after health. Returns false when hooks failed
        /// and <see cref="AppConfig.PostDeployHookFailureFailsDeploy"/> is true (result already filled).
        /// </summary>
        private bool TryRunPostDeployHooks(DeploymentRequest request, DeploymentResult result, CancellationToken token)
        {
            string raw = PostDeployHookRunner.ResolveHookList(request);
            string[] hooks = PostDeployHookRunner.ParseHookList(raw);
            if (hooks.Length == 0)
            {
                return true;
            }

            Report(DeploymentStep.PostDeployHook, "Running post-deploy hooks...", 97, false);
            token.ThrowIfCancellationRequested();

            var run = PostDeployHookRunner.RunAll(hooks, AppConfig.PostDeployHookTimeoutSeconds);
            result.PostDeployHookExitCode = run.ExitCode;
            result.PostDeployHookSummary = run.Summary;

            if (run.ExitCode == 0)
            {
                Log.Information("Post-deploy hooks succeeded.");
                return true;
            }

            Log.Warning("Post-deploy hooks reported exit {Exit}: {Summary}", run.ExitCode, run.Summary);
            if (AppConfig.PostDeployHookFailureFailsDeploy)
            {
                result.Success = false;
                result.FailedAt = DeploymentStep.PostDeployHook;
                result.ErrorMessage = "Post-deploy hook failed (exit " + run.ExitCode + "): " + run.Summary;
                Report(DeploymentStep.PostDeployHook, "FAILED: " + result.ErrorMessage, 100, false);
                Log.Error("=== Deployment failed at PostDeployHook === App: {App}", request.ApplicationName);
                return false;
            }

            return true;
        }

        private static void ValidateInputs(DeploymentRequest request)
        {
            if (!ValidationHelper.IsSafeName(request.ApplicationName))
            {
                throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                    "Application name is empty or contains invalid characters.");
            }
            if (!ValidationHelper.IsSafeVersion(request.Version))
            {
                throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                    "Version is empty or contains invalid characters.");
            }

            DeploymentMode mode = request.Mode;

            if (DeploymentPlan.NeedsSourceProbe(mode) || DeploymentPlan.RunsCopy(mode))
            {
                if (!ValidationHelper.DirectoryExists(request.SourceFolder))
                {
                    throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                        "Source folder does not exist: " + request.SourceFolder);
                }
            }

            if (DeploymentPlan.NeedsIisAvailability(mode))
            {
                if (request.Iis.Mode == IisMode.ApplicationUnderSite)
                {
                    if (string.IsNullOrWhiteSpace(request.Iis.ParentSiteName) ||
                        !ValidationHelper.IsSafeName(request.Iis.ParentSiteName))
                    {
                        throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                            "Parent IIS site name is empty or contains invalid characters.");
                    }
                    if (!ValidationHelper.IsSafeApplicationPath(request.Iis.ApplicationPath))
                    {
                        throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                            "Application path is invalid. Use a path like /BiCore (leading slash, no backslashes, " +
                            "no '..' segments, no trailing dots or spaces).");
                    }
                    // The operator's port field is meaningless for an application under a site:
                    // the parent site's binding defines the URL. Ignore rather than misuse it.
                    request.Iis.Port = 0;
                    request.Iis.HttpsPort = 0;
                    request.Iis.HttpsCertificateThumbprint = null;
                }
                else
                {
                    if (!ValidationHelper.IsSafeName(request.Iis.SiteName))
                    {
                        throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                            "IIS site name is empty or contains invalid characters.");
                    }
                    if (request.Iis.Port < 1 || request.Iis.Port > 65535)
                    {
                        throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                            "Port must be between 1 and 65535.");
                    }
                    if (request.Iis.HttpsPort != 0)
                    {
                        if (request.Iis.HttpsPort < 1 || request.Iis.HttpsPort > 65535)
                        {
                            throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                                "HTTPS port must be between 1 and 65535 (or leave blank for HTTP only).");
                        }
                        if (request.Iis.HttpsPort == request.Iis.Port)
                        {
                            throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                                "HTTPS port must differ from the HTTP port.");
                        }
                        if (string.IsNullOrWhiteSpace(request.Iis.HttpsCertificateThumbprint))
                        {
                            throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                                "HTTPS port is set but no certificate thumbprint was provided.");
                        }
                        if (!IisService.CertificateExists(
                                request.Iis.HttpsCertificateThumbprint,
                                request.Iis.ResolvedHttpsCertificateStoreName))
                        {
                            throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                                "HTTPS certificate was not found in LocalMachine\\" +
                                request.Iis.ResolvedHttpsCertificateStoreName + ".");
                        }
                    }
                }

                if (!ValidationHelper.IsSafeName(request.Iis.ApplicationPoolName))
                {
                    throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                        "Application pool name is empty or contains invalid characters.");
                }
            }

            if (DeploymentPlan.RunsCopy(mode) || DeploymentPlan.RunsIisConfigure(mode)
                || mode == DeploymentMode.RecycleOnly)
            {
                if (string.IsNullOrWhiteSpace(request.DeploymentRoot) || !Path.IsPathRooted(request.DeploymentRoot))
                {
                    throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                        "Deployment location must be an absolute path, e.g. C:\\IntraDeploy\\Applications.");
                }
            }

            if (DeploymentPlan.WillRestoreDatabase(request)
                || (DeploymentPlan.RunsDatabase(mode) && request.Database != null
                    && request.Database.Mode == DatabaseMode.RestoreFromBak))
            {
                if (!ValidationHelper.IsSafeName(request.Database.DatabaseName))
                {
                    throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                        "Database name is empty or contains invalid characters.");
                }
                if (!ValidationHelper.FileExists(request.Database.BackupFilePath))
                {
                    throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                        "Backup file does not exist: " + request.Database.BackupFilePath);
                }
            }

            if (mode == DeploymentMode.DatabaseOnly
                && (request.Database == null || request.Database.Mode != DatabaseMode.RestoreFromBak))
            {
                throw new DeploymentStepException(DeploymentStep.ValidateInputs,
                    "Database only mode requires Restore database from .bak.");
            }
        }

        /// <summary>
        /// Resolves the health-check path: request override when set, otherwise App.config.
        /// </summary>
        public static string ResolveHealthCheckPath(DeploymentRequest request)
        {
            if (request != null && !string.IsNullOrWhiteSpace(request.HealthCheckPath))
            {
                return request.HealthCheckPath.Trim();
            }
            return AppConfig.HealthCheckPath;
        }

        /// <summary>
        /// Appends a relative health path to the site/app base URL.
        /// "/" or empty leaves the base URL unchanged (aside from trimming).
        /// </summary>
        public static string CombineHealthCheckUrl(string baseUrl, string healthPath)
        {
            string basePart = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim();
            if (string.IsNullOrWhiteSpace(healthPath))
            {
                return basePart;
            }

            string path = healthPath.Trim();
            if (path == "/" || path.Length == 0)
            {
                return basePart;
            }

            if (!path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "/" + path;
            }

            return basePart.TrimEnd('/') + path;
        }

        /// <summary>
        /// Health GET with retries/backoff. Unreachable and UnhealthyResponse retry;
        /// Healthy returns immediately. Final outcome drives fail-vs-warn policy.
        /// </summary>
        private static (HealthCheckOutcome Outcome, string Summary) PerformHealthCheckWithRetries(
            string url,
            int timeoutMilliseconds)
        {
            int attempts = AppConfig.HealthCheckRetries;
            int delayMs = AppConfig.HealthCheckRetryDelayMilliseconds;
            (HealthCheckOutcome Outcome, string Summary) last =
                (HealthCheckOutcome.Unreachable, "Health check did not run.");

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                last = PerformBasicHealthCheck(url, timeoutMilliseconds);
                if (last.Outcome == HealthCheckOutcome.Healthy)
                {
                    string summary = last.Summary;
                    if (attempts > 1)
                    {
                        summary = summary + " (attempt " + attempt + "/" + attempts + ")";
                    }
                    return (last.Outcome, summary);
                }

                Log.Warning(
                    "Health check attempt {Attempt}/{Total} for {Url}: {Outcome} — {Summary}",
                    attempt, attempts, url, last.Outcome, last.Summary);

                if (attempt < attempts && delayMs > 0)
                {
                    Thread.Sleep(delayMs * attempt);
                }
            }

            return (last.Outcome,
                last.Summary + " (after " + attempts + " attempt(s))");
        }

        /// <summary>
        /// Basic health validation. Classification:
        ///   2xx/3xx          → Healthy
        ///   4xx/5xx response → UnhealthyResponse (IIS is serving; the application failed)
        ///   transport error  → Unreachable
        /// </summary>
        private static (HealthCheckOutcome Outcome, string Summary) PerformBasicHealthCheck(
            string url,
            int timeoutMilliseconds)
        {
            try
            {
                System.Net.HttpWebRequest webRequest =
                    (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url);
                webRequest.Method = "GET";
                webRequest.Timeout = timeoutMilliseconds > 0 ? timeoutMilliseconds : 15000;
                webRequest.AllowAutoRedirect = false;
                webRequest.UserAgent = "IntraDeploy health check";

                using (System.Net.HttpWebResponse response =
                    (System.Net.HttpWebResponse)webRequest.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    Log.Information("Health check: {Url} responded {StatusCode} {Description} (timeout {TimeoutMs}ms)",
                        url, status, response.StatusDescription, webRequest.Timeout);
                    if (status < 400)
                    {
                        return (HealthCheckOutcome.Healthy,
                            "HTTP " + status + " (" + response.StatusDescription + ") from " + url);
                    }
                    return (HealthCheckOutcome.UnhealthyResponse,
                        "HTTP " + status + " (" + response.StatusDescription + ") from " + url);
                }
            }
            catch (System.Net.WebException wex)
            {
                System.Net.HttpWebResponse errorResponse = wex.Response as System.Net.HttpWebResponse;
                if (errorResponse != null)
                {
                    int status = (int)errorResponse.StatusCode;
                    Log.Warning("Health check: {Url} responded {StatusCode} (application returned an error)",
                        url, status);
                    return (HealthCheckOutcome.UnhealthyResponse,
                        "HTTP " + status + " (" + errorResponse.StatusDescription + ") from " + url +
                        " — IIS is serving but the application reported an error.");
                }

                Log.Error(wex, "Health check failed for {Url}: {Message}", url, wex.Message);
                return (HealthCheckOutcome.Unreachable,
                    "Health check could not reach " + url + ": " + wex.Message);
            }
        }

        private void Report(DeploymentStep step, string message, int percent, bool indeterminate)
        {
            Progress?.Invoke(this, new ProgressEventArgs(step, message, percent, indeterminate));
        }
    }
}
