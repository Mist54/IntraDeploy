using System;
using Serilog;
using IntraDeploy.Models;
using IntraDeploy.Utilities;

namespace IntraDeploy.Services
{
    /// <summary>
    /// Read-only pre-flight checks. Never copies files, never mutates IIS or SQL.
    /// </summary>
    public class PreflightService
    {
        private readonly ApplicationDetector _applicationDetector;
        private readonly IisService _iisService;
        private readonly DatabaseService _databaseService;

        public PreflightService(
            ApplicationDetector applicationDetector,
            IisService iisService,
            DatabaseService databaseService)
        {
            _applicationDetector = applicationDetector;
            _iisService = iisService;
            _databaseService = databaseService;
        }

        /// <summary>
        /// Runs elevation, IIS, probe, Core hosting, port conflict, optional SQL,
        /// PortHelper soft checks, and existing app-pool CLR mismatch gate.
        /// Does not throw for check failures — they become findings.
        /// </summary>
        public PreflightResult Validate(DeploymentRequest request)
        {
            var result = new PreflightResult();

            if (request == null)
            {
                result.Add(PreflightSeverity.Error, "Input", "Deployment request is missing.");
                return result;
            }

            using (Serilog.Context.LogContext.PushProperty("RunId", request.RunId ?? "preflight"))
            {
                Log.Information("=== Pre-flight validate started === App: {App} v{Version} mode={Mode} dryRun={DryRun}",
                    request.ApplicationName, request.Version, request.Mode, request.DryRun);

                CheckElevation(result);
                CheckInputs(request, result);

                ApplicationProbeResult probe = null;
                if (DeploymentPlan.NeedsSourceProbe(request.Mode))
                {
                    probe = CheckProbe(request, result);
                }
                else
                {
                    result.Add(PreflightSeverity.Info, "Probe",
                        "Skipped (mode " + DeploymentPlan.DescribeMode(request.Mode) + " does not require published folder).");
                }

                if (DeploymentPlan.NeedsIisAvailability(request.Mode))
                {
                    CheckIis(request, result);
                    if (DeploymentPlan.RunsIisConfigure(request.Mode))
                    {
                        CheckCoreHosting(probe, result);
                        CheckApplicationPoolClr(request, probe, result);
                        CheckPortConflict(request, result);
                        CheckPortInUseSoft(request, result);
                        CheckHttpsCertificate(request, result);
                    }
                }
                else
                {
                    result.Add(PreflightSeverity.Info, "IIS",
                        "Skipped (mode does not require IIS for this run).");
                }

                if (request.Mode == DeploymentMode.DatabaseOnly
                    && (request.Database == null || request.Database.Mode != DatabaseMode.RestoreFromBak))
                {
                    result.Add(PreflightSeverity.Error, "Database",
                        "Database only mode requires Restore database from .bak.");
                }
                else if (DeploymentPlan.RunsDatabase(request.Mode))
                {
                    CheckSqlIfNeeded(request, result);
                }
                else
                {
                    result.Add(PreflightSeverity.Info, "Database",
                        "SQL checks skipped (mode does not include database).");
                }

                if (DeploymentPlan.RunsApplyConfiguration(request.Mode))
                {
                    CheckConnectionStringIntent(request, result);
                }

                Log.Information("=== Pre-flight validate finished === errors={Errors} warnings={Warnings} continueRequired={Continue}",
                    result.HasErrors, result.HasWarnings, result.HasContinueRequiredWarnings);
            }

            return result;
        }

        /// <summary>
        /// Probe + existing application pool CLR mismatch only.
        /// Used by Deploy before confirmation so operators must explicitly continue on mismatch.
        /// </summary>
        public PreflightResult CheckPoolGate(DeploymentRequest request)
        {
            var result = new PreflightResult();
            if (request == null)
            {
                result.Add(PreflightSeverity.Error, "Input", "Deployment request is missing.");
                return result;
            }

            using (Serilog.Context.LogContext.PushProperty("RunId", request.RunId ?? "pool-gate"))
            {
                ApplicationProbeResult probe = CheckProbe(request, result);
                CheckApplicationPoolClr(request, probe, result);
            }

            return result;
        }

        private static void CheckElevation(PreflightResult result)
        {
            if (ElevationHelper.IsElevated)
            {
                result.Add(PreflightSeverity.Info, "Elevation", "Running as administrator.");
            }
            else
            {
                result.Add(PreflightSeverity.Warning, "Elevation",
                    "Not elevated. IIS configuration will fail until IntraDeploy is restarted as administrator.");
            }
        }

        private static void CheckInputs(DeploymentRequest request, PreflightResult result)
        {
            DeploymentMode mode = request.Mode;

            if (!ValidationHelper.IsSafeName(request.ApplicationName))
            {
                result.Add(PreflightSeverity.Error, "Input", "Application name is empty or contains invalid characters.");
            }
            if (!ValidationHelper.IsSafeVersion(request.Version))
            {
                result.Add(PreflightSeverity.Error, "Input", "Version is empty or contains invalid characters.");
            }

            if (DeploymentPlan.NeedsSourceProbe(mode) || DeploymentPlan.RunsCopy(mode))
            {
                if (!ValidationHelper.DirectoryExists(request.SourceFolder))
                {
                    result.Add(PreflightSeverity.Error, "Input", "Source folder does not exist.");
                }
            }

            if (DeploymentPlan.RunsCopy(mode) || DeploymentPlan.RunsIisConfigure(mode)
                || mode == DeploymentMode.RecycleOnly)
            {
                if (string.IsNullOrWhiteSpace(request.DeploymentRoot) || !System.IO.Path.IsPathRooted(request.DeploymentRoot))
                {
                    result.Add(PreflightSeverity.Error, "Input", "Deployment location must be an absolute path.");
                }
            }

            if (!DeploymentPlan.NeedsIisAvailability(mode))
            {
                if (DeploymentPlan.WillRestoreDatabase(request)
                    || (mode == DeploymentMode.DatabaseOnly && request.Database != null
                        && request.Database.Mode == DatabaseMode.RestoreFromBak))
                {
                    if (request.Database == null || !ValidationHelper.IsSafeName(request.Database.DatabaseName))
                    {
                        result.Add(PreflightSeverity.Error, "Input", "Database name is empty or invalid.");
                    }
                    if (request.Database == null || !ValidationHelper.FileExists(request.Database.BackupFilePath))
                    {
                        result.Add(PreflightSeverity.Error, "Input", "Backup file does not exist.");
                    }
                }
                return;
            }

            if (request.Iis == null)
            {
                result.Add(PreflightSeverity.Error, "Input", "IIS settings are missing.");
                return;
            }

            if (request.Iis.Mode == IisMode.ApplicationUnderSite)
            {
                if (!ValidationHelper.IsSafeName(request.Iis.ParentSiteName))
                {
                    result.Add(PreflightSeverity.Error, "Input", "Parent IIS site name is empty or invalid.");
                }
                if (!ValidationHelper.IsSafeApplicationPath(request.Iis.ApplicationPath))
                {
                    result.Add(PreflightSeverity.Error, "Input", "Application path is invalid (use e.g. /BiCore).");
                }
            }
            else
            {
                if (!ValidationHelper.IsSafeName(request.Iis.SiteName))
                {
                    result.Add(PreflightSeverity.Error, "Input", "IIS site name is empty or invalid.");
                }
                if (request.Iis.Port < 1 || request.Iis.Port > 65535)
                {
                    result.Add(PreflightSeverity.Error, "Input", "Port must be between 1 and 65535.");
                }
                if (request.Iis.HttpsPort != 0)
                {
                    if (request.Iis.HttpsPort < 1 || request.Iis.HttpsPort > 65535)
                    {
                        result.Add(PreflightSeverity.Error, "Input", "HTTPS port must be between 1 and 65535 (or blank/0 for HTTP only).");
                    }
                    else if (request.Iis.HttpsPort == request.Iis.Port)
                    {
                        result.Add(PreflightSeverity.Error, "Input",
                            "HTTPS port must differ from the HTTP port.");
                    }
                    if (string.IsNullOrWhiteSpace(request.Iis.HttpsCertificateThumbprint))
                    {
                        result.Add(PreflightSeverity.Error, "Input",
                            "HTTPS port is set but no certificate was selected.");
                    }
                }
            }

            if (!ValidationHelper.IsSafeName(request.Iis.ApplicationPoolName))
            {
                result.Add(PreflightSeverity.Error, "Input", "Application pool name is empty or invalid.");
            }

            if (DeploymentPlan.WillRestoreDatabase(request)
                || (mode == DeploymentMode.DatabaseOnly && request.Database != null
                    && request.Database.Mode == DatabaseMode.RestoreFromBak))
            {
                if (request.Database == null || !ValidationHelper.IsSafeName(request.Database.DatabaseName))
                {
                    result.Add(PreflightSeverity.Error, "Input", "Database name is empty or invalid.");
                }
                if (request.Database == null || !ValidationHelper.FileExists(request.Database.BackupFilePath))
                {
                    result.Add(PreflightSeverity.Error, "Input", "Backup file does not exist.");
                }
            }
        }

        private ApplicationProbeResult CheckProbe(DeploymentRequest request, PreflightResult result)
        {
            if (string.IsNullOrWhiteSpace(request.SourceFolder) || !System.IO.Directory.Exists(request.SourceFolder))
            {
                return null;
            }

            try
            {
                ApplicationProbeResult probe = _applicationDetector.Probe(request.SourceFolder);
                if (!probe.IsValid)
                {
                    result.Add(PreflightSeverity.Error, "Probe", probe.ErrorMessage ?? "Published folder is not a recognized application.");
                    return probe;
                }

                result.Add(PreflightSeverity.Info, "Probe", "Detected: " + probe.Info.Description);
                return probe;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pre-flight probe failed");
                result.Add(PreflightSeverity.Error, "Probe", "Could not analyze published folder: " + ex.Message);
                return null;
            }
        }

        private void CheckIis(DeploymentRequest request, PreflightResult result)
        {
            try
            {
                if (_iisService.IsIisAvailable())
                {
                    result.Add(PreflightSeverity.Info, "IIS", "IIS is available.");
                }
                else
                {
                    result.Add(PreflightSeverity.Error, "IIS",
                        "IIS is not installed or cannot be accessed. Install IIS with application support and run as administrator.");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pre-flight IIS check failed");
                result.Add(PreflightSeverity.Error, "IIS", "IIS check failed: " + ex.Message);
            }
        }

        private void CheckCoreHosting(ApplicationProbeResult probe, PreflightResult result)
        {
            if (probe == null || !probe.IsValid || probe.Info == null)
            {
                return;
            }

            string warning = _applicationDetector.GetPrerequisiteWarning(probe.Info);
            if (warning != null)
            {
                // Deploy treats this as a hard stop; pre-flight surfaces it as Error so Validate matches.
                result.Add(PreflightSeverity.Error, "ASP.NET Core", warning);
            }
        }

        private void CheckApplicationPoolClr(
            DeploymentRequest request,
            ApplicationProbeResult probe,
            PreflightResult result)
        {
            if (request.Iis == null || string.IsNullOrWhiteSpace(request.Iis.ApplicationPoolName))
            {
                return;
            }
            if (probe == null || !probe.IsValid || probe.Info == null)
            {
                return;
            }

            try
            {
                string mismatch = _iisService.GetApplicationPoolClrMismatch(
                    request.Iis.ApplicationPoolName, probe.Info.Type);
                if (mismatch != null)
                {
                    result.Add(
                        PreflightSeverity.Warning,
                        "Application Pool",
                        mismatch,
                        requiresExplicitContinue: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pre-flight application pool CLR check failed for {Pool}",
                    request.Iis.ApplicationPoolName);
                result.Add(PreflightSeverity.Warning, "Application Pool",
                    "Could not check existing application pool CLR settings: " + ex.Message);
            }
        }

        private void CheckPortConflict(DeploymentRequest request, PreflightResult result)
        {
            if (request.Iis == null || request.Iis.Mode != IisMode.SeparateSite)
            {
                return;
            }

            try
            {
                string conflict = _iisService.GetPortConflict(request);
                if (conflict != null)
                {
                    result.Add(PreflightSeverity.Error, "IIS binding", conflict);
                }
                else
                {
                    string msg = "No IIS site binding conflict on HTTP port " + request.Iis.Port + ".";
                    if (request.Iis.HasHttpsBinding)
                    {
                        msg += " HTTPS port " + request.Iis.HttpsPort + " also clear.";
                    }
                    result.Add(PreflightSeverity.Info, "IIS binding", msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pre-flight port conflict check failed");
                result.Add(PreflightSeverity.Warning, "IIS binding",
                    "Could not check IIS binding conflicts: " + ex.Message);
            }
        }

        private static void CheckPortInUseSoft(DeploymentRequest request, PreflightResult result)
        {
            if (request.Iis == null || request.Iis.Mode != IisMode.SeparateSite)
            {
                return;
            }

            CheckOnePortSoft(request.Iis.Port, "HTTP", result);
            if (request.Iis.HasHttpsBinding)
            {
                CheckOnePortSoft(request.Iis.HttpsPort, "HTTPS", result);
            }
        }

        private static void CheckOnePortSoft(int port, string label, PreflightResult result)
        {
            if (port < 1 || port > 65535)
            {
                return;
            }

            try
            {
                if (PortHelper.IsPortInUse(port))
                {
                    result.Add(PreflightSeverity.Warning, "Port",
                        label + " TCP port " + port + " appears to be in use on this machine (soft check). " +
                        "Deploy may still succeed if the listener is the same site; otherwise choose another port.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PortHelper check failed for port {Port}", port);
                result.Add(PreflightSeverity.Warning, "Port",
                    "Could not check whether " + label + " port " + port + " is in use: " + ex.Message);
            }
        }

        private void CheckHttpsCertificate(DeploymentRequest request, PreflightResult result)
        {
            if (request.Iis == null || request.Iis.Mode != IisMode.SeparateSite || !request.Iis.HasHttpsBinding)
            {
                return;
            }

            string thumb = request.Iis.HttpsCertificateThumbprint;
            string store = request.Iis.ResolvedHttpsCertificateStoreName;
            if (string.IsNullOrWhiteSpace(thumb))
            {
                return;
            }

            try
            {
                string normalized = IisService.NormalizeThumbprint(thumb);
                if (IisService.CertificateExists(normalized, store))
                {
                    string tip = normalized.Length <= 8 ? normalized : normalized.Substring(normalized.Length - 8);
                    result.Add(PreflightSeverity.Info, "HTTPS certificate",
                        "Certificate found in LocalMachine\\" + store + " (…" + tip + ").");
                }
                else
                {
                    result.Add(PreflightSeverity.Error, "HTTPS certificate",
                        "Certificate thumbprint not found in LocalMachine\\" + store +
                        ". Select a certificate that exists on this machine (with a private key for IIS).");
                }
            }
            catch (Exception ex)
            {
                result.Add(PreflightSeverity.Warning, "HTTPS certificate",
                    "Could not verify certificate: " + ex.Message);
            }
        }

        private void CheckSqlIfNeeded(DeploymentRequest request, PreflightResult result)
        {
            if (request.Database == null || request.Database.Mode != DatabaseMode.RestoreFromBak)
            {
                result.Add(PreflightSeverity.Info, "Database",
                    "Database mode is Use existing — no SQL connection or backup checks.");
                return;
            }

            try
            {
                _databaseService.TestConnection(request.Database);
                result.Add(PreflightSeverity.Info, "Database",
                    "SQL Server connection succeeded for '" + request.Database.Server + "'.");
            }
            catch (DeploymentStepException ex)
            {
                result.Add(PreflightSeverity.Error, "Database", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pre-flight SQL TestConnection failed for {Server}", request.Database.Server);
                result.Add(PreflightSeverity.Error, "Database",
                    "SQL Server connection failed for '" + request.Database.Server + "'.");
                return;
            }

            try
            {
                RestorePlan plan = _databaseService.ValidateBackup(request.Database);
                string detail = plan != null && !string.IsNullOrEmpty(plan.LogicalDataFile)
                    ? "logical data file '" + plan.LogicalDataFile + "'"
                    : "header read OK";
                result.Add(PreflightSeverity.Info, "Database",
                    "Backup file validated (" + detail + "). No restore was performed.");
            }
            catch (DeploymentStepException ex)
            {
                result.Add(PreflightSeverity.Error, "Database", ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pre-flight ValidateBackup failed");
                result.Add(PreflightSeverity.Error, "Database", "Backup validation failed: " + ex.Message);
            }
        }

        private static void CheckConnectionStringIntent(DeploymentRequest request, PreflightResult result)
        {
            if (!request.UpdateConnectionString)
            {
                result.Add(PreflightSeverity.Info, "Configuration",
                    "Connection string will not be changed in the deployed copy.");
                return;
            }

            if (string.IsNullOrWhiteSpace(request.ConnectionString))
            {
                result.Add(PreflightSeverity.Error, "Configuration",
                    "Update connection string is enabled but the connection string is empty.");
                return;
            }

            // Never echo the connection string (may contain a password).
            result.Add(PreflightSeverity.Info, "Configuration",
                "Connection string update is enabled for the deployed copy only (source publish folder untouched).");
        }
    }
}
