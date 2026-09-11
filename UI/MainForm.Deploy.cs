using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Krypton.Toolkit;
using IntraDeploy.Configuration;
using IntraDeploy.Models;
using IntraDeploy.Services;
using IntraDeploy.Utilities;
using Serilog;

namespace IntraDeploy.UI
{
    public partial class MainForm
    {
        private async void ValidateButton_Click(object sender, EventArgs e)
        {
            if (_isDeploying)
            {
                return;
            }

            DeploymentRequest request = BuildRequest();
            if (request == null)
            {
                return;
            }

            SetStatus("Running pre-flight Validate (no writes)...", true);
            _validateButton.Enabled = false;
            deployButton.Enabled = false;
            if (_rollbackButton != null) { _rollbackButton.Enabled = false; }
            if (_recentButton != null) { _recentButton.Enabled = false; }

            PreflightResult preflight;
            try
            {
                preflight = await Task.Run(() => _preflightService.Validate(request));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Pre-flight Validate failed unexpectedly.");
                SetStatus("Validate failed: " + ex.Message, false);
                _validateButton.Enabled = true;
                deployButton.Enabled = true;
                if (_rollbackButton != null) { _rollbackButton.Enabled = true; }
                if (_recentButton != null) { _recentButton.Enabled = true; }
                KryptonMessageBox.Show(this, "Validate failed unexpectedly:\n\n" + ex.Message,
                    "Validate", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Error);
                return;
            }

            _validateButton.Enabled = true;
            deployButton.Enabled = true;
            if (_rollbackButton != null) { _rollbackButton.Enabled = true; }
            if (_recentButton != null) { _recentButton.Enabled = true; }

            if (preflight.HasErrors)
            {
                SetStatus("Validate found errors â€” see dialog.", false);
            }
            else if (preflight.HasContinueRequiredWarnings)
            {
                SetStatus("Validate OK with continue-required warnings.", false);
            }
            else if (preflight.HasWarnings)
            {
                SetStatus("Validate OK with warnings.", false);
            }
            else
            {
                SetStatus("Validate passed.", false);
            }

            ShowPreflightResult(preflight);
        }

        private void ShowPreflightResult(PreflightResult preflight)
        {
            var sb = new System.Text.StringBuilder();
            if (preflight.HasErrors)
            {
                sb.AppendLine("Result: FAILED (errors present). No changes were made.");
            }
            else if (preflight.HasContinueRequiredWarnings)
            {
                sb.AppendLine("Result: PASSED with CONTINUE-REQUIRED warnings. No changes were made.");
                sb.AppendLine("Deploy will ask you to continue explicitly before changing anything.");
            }
            else if (preflight.HasWarnings)
            {
                sb.AppendLine("Result: PASSED with warnings. No changes were made.");
            }
            else
            {
                sb.AppendLine("Result: PASSED. No changes were made.");
            }
            sb.AppendLine();

            foreach (PreflightFinding finding in preflight.Findings)
            {
                sb.Append('[').Append(finding.Severity.ToString().ToUpperInvariant()).Append("] ");
                if (finding.RequiresExplicitContinue)
                {
                    sb.Append("[CONTINUE REQUIRED] ");
                }
                sb.Append(finding.Area).Append(": ").AppendLine(finding.Message);
            }

            KryptonMessageBoxIcon icon = preflight.HasErrors
                ? KryptonMessageBoxIcon.Error
                : (preflight.HasWarnings ? KryptonMessageBoxIcon.Warning : KryptonMessageBoxIcon.Information);

            KryptonMessageBox.Show(this, sb.ToString(), "Pre-flight Validate",
                KryptonMessageBoxButtons.OK, icon);
        }

        private async void DeployButton_Click(object sender, EventArgs e)
        {
            if (_isDeploying)
            {
                return;
            }

            DeploymentRequest request = BuildRequest();
            if (request == null)
            {
                return;
            }

            // Same-version target folder: require explicit overwrite confirmation (file modes only).
            if (DeploymentPlan.RunsCopy(request.Mode)
                && !request.DryRun
                && Directory.Exists(request.TargetFolder)
                && !request.AllowOverwriteExistingFolder)
            {
                DialogResult overwrite = KryptonMessageBox.Show(
                    this,
                    "Deployment folder already exists:\n\n" + request.TargetFolder + "\n\n" +
                    "Yes = Overwrite existing version   (the running application will be stopped first)\n" +
                    "No = Cancel deployment",
                    "Deployment folder already exists",
                    KryptonMessageBoxButtons.YesNo,
                    KryptonMessageBoxIcon.Warning,
                    KryptonMessageBoxDefaultButton.Button2);

                if (overwrite != DialogResult.Yes)
                {
                    Log.Information("Deployment cancelled: operator declined overwriting existing folder {Folder}",
                        request.TargetFolder);
                    return;
                }

                request.AllowOverwriteExistingFolder = true;
            }

            // Existing app-pool CLR mismatch: hard gate â€” never mutate the pool; require Continue.
            // Skip for dry-run and modes that do not configure IIS.
            if (!request.DryRun && DeploymentPlan.RunsIisConfigure(request.Mode))
            {
                SetStatus("Checking application pool compatibility...", true);
                PreflightResult poolGate;
                try
                {
                    poolGate = await Task.Run(() => _preflightService.CheckPoolGate(request));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Pool gate pre-flight failed unexpectedly.");
                    SetStatus("Pool check failed: " + ex.Message, false);
                    KryptonMessageBox.Show(this, "Could not check the application pool:\n\n" + ex.Message,
                        "Application pool check", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Error);
                    return;
                }
                SetStatus("Ready", false);

                if (poolGate.HasContinueRequiredWarnings)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("The existing application pool does not match this application type.");
                    sb.AppendLine("IntraDeploy will NOT change the pool CLR settings.");
                    sb.AppendLine();
                    foreach (PreflightFinding finding in poolGate.Findings)
                    {
                        if (finding.RequiresExplicitContinue)
                        {
                            sb.AppendLine(finding.Message);
                            sb.AppendLine();
                        }
                    }
                    sb.Append("Yes = Continue deploy anyway    No = Cancel");

                    DialogResult continueDeploy = KryptonMessageBox.Show(
                        this,
                        sb.ToString(),
                        "Application pool CLR mismatch",
                        KryptonMessageBoxButtons.YesNo,
                        KryptonMessageBoxIcon.Warning,
                        KryptonMessageBoxDefaultButton.Button2);

                    if (continueDeploy != DialogResult.Yes)
                    {
                        Log.Information(
                            "Deployment cancelled: operator declined pool CLR mismatch continue for pool {Pool}",
                            request.Iis.ApplicationPoolName);
                        return;
                    }

                    Log.Warning(
                        "Operator explicitly continued despite application pool CLR mismatch for pool {Pool}",
                        request.Iis.ApplicationPoolName);
                }
            }

            // Database existence check for restore mode, executed OFF the UI thread.
            Func<string, bool> databaseExistsCheck = null;
            if (!request.DryRun && DeploymentPlan.WillRestoreDatabase(request))
            {
                SetStatus("Checking database on " + request.Database.Server + "...", true);

                DatabaseSettings dbSettings = request.Database;
                bool dbExists = await Task.Run(() =>
                {
                    try
                    {
                        return _databaseService.DatabaseExists(dbSettings);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Database existence check failed for {Server}", dbSettings.Server);
                        throw;
                    }
                });

                SetStatus("Ready", false);

                if (dbExists)
                {
                    // The confirmation dialog will require the explicit overwrite checkbox.
                    databaseExistsCheck = dbName => true;
                }
            }

            // Live IIS state for the confirmation dialog (when mode touches IIS).
            IisLiveState liveState = null;
            if (DeploymentPlan.NeedsIisAvailability(request.Mode))
            {
                SetStatus("Checking IIS state...", true);
                liveState = await Task.Run(() =>
                {
                    try
                    {
                        return _iisService.GetLiveState(request);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "IIS live-state check failed.");
                        return null;
                    }
                });
                SetStatus("Ready", false);
            }

            using (var confirmDialog = new DeploymentConfirmationDialog(request, databaseExistsCheck, liveState))
            {
                if (confirmDialog.ShowDialog(this) != DialogResult.Yes)
                {
                    Log.Information("Deployment cancelled at confirmation dialog. App: {App}", request.ApplicationName);
                    return;
                }
            }

            await RunDeploymentAsync(request);
        }

        private void CancelDeployButton_Click(object sender, EventArgs e)
        {
            if (_cancellationTokenSource != null)
            {
                Log.Information("Operator requested deployment cancellation.");
                _cancellationTokenSource.Cancel();
                SetStatus("Cancelling after current file...", false);
            }
        }

        // ------------------------------------------------------------------
        // Deployment execution
        // ------------------------------------------------------------------

        private DeploymentRequest BuildRequest()
        {
            bool appUnderSite = _appUnderSiteRadio.Checked;

            var request = new DeploymentRequest
            {
                ApplicationName = applicationNameCombo.Text.Trim(),
                Version = versionTextBox.Text.Trim(),
                SourceFolder = sourceFolderTextBox.Text.Trim(),
                DeploymentRoot = deploymentRootTextBox.Text.Trim(),
                Iis = new IisSettings
                {
                    Mode = appUnderSite ? IisMode.ApplicationUnderSite : IisMode.SeparateSite,
                    ParentSiteName = _parentSiteCombo.Text.Trim(),
                    ApplicationPath = _applicationPathTextBox.Text.Trim(),
                    SiteName = siteNameTextBox.Text.Trim(),
                    ApplicationPoolName = poolNameTextBox.Text.Trim(),
                    Port = ParsePort(),
                    HttpsPort = ParseHttpsPort(),
                    HttpsCertificateThumbprint = ParseHttpsPort() > 0 ? GetSelectedHttpsThumbprint() : null,
                    DeploymentRoot = deploymentRootTextBox.Text.Trim()
                },
                Database = new DatabaseSettings
                {
                    Server = sqlServerTextBox.Text.Trim(),
                    DatabaseName = databaseNameTextBox.Text.Trim(),
                    Mode = restoreFromBakRadio.Checked ? DatabaseMode.RestoreFromBak : DatabaseMode.UseExisting,
                    BackupFilePath = backupFileTextBox.Text.Trim(),
                    SqlUser = _sqlAuthRadio.Checked ? _sessionSqlUser : null,
                    SqlPassword = _sqlAuthRadio.Checked ? _sessionSqlPassword : null
                },
                UpdateConnectionString = _updateConnectionStringCheck.Checked,
                ConnectionString = _updateConnectionStringCheck.Checked
                    ? _connectionStringTextBox.Text.Trim()
                    : null,
                HealthCheckPath = _healthCheckPathTextBox != null
                    ? _healthCheckPathTextBox.Text.Trim()
                    : null,
                // null = use App.config PostDeployHooks
                PostDeployHooks = null,
                Mode = GetSelectedDeployMode(),
                DryRun = _dryRunCheck != null && _dryRunCheck.Checked,
                RunId = Guid.NewGuid().ToString("N").Substring(0, 8)
            };

            if (_sqlAuthRadio.Checked && string.IsNullOrWhiteSpace(request.Database.SqlUser))
            {
                KryptonMessageBox.Show(this,
                    "SQL Server login is selected but no credentials are set for this session.\n\nClick Set login... first.",
                    "SQL authentication",
                    KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                return null;
            }

            if (request.UpdateConnectionString && string.IsNullOrWhiteSpace(request.ConnectionString))
            {
                KryptonMessageBox.Show(this,
                    "Update connection string is enabled but the connection string is empty.\n\nEnter one or click Build....",
                    "Connection string",
                    KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                return null;
            }

            string validationError = ValidateUiInput(request);
            if (validationError != null)
            {
                KryptonMessageBox.Show(this, validationError, "Invalid input",
                    KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                return null;
            }

            // Remember the operator's locations for the next session (best effort).
            RecentPathsStore.Set(RecentKeyDeploymentRoot, request.DeploymentRoot);
            if (Directory.Exists(request.SourceFolder))
            {
                RecentPathsStore.Set(RecentKeySourceFolder, request.SourceFolder);
            }
            if (request.Database.Mode == DatabaseMode.RestoreFromBak && File.Exists(request.Database.BackupFilePath))
            {
                string backupFolder = Path.GetDirectoryName(request.Database.BackupFilePath);
                if (!string.IsNullOrEmpty(backupFolder))
                {
                    RecentPathsStore.Set(RecentKeyBackupFolder, backupFolder);
                }
            }

            return request;
        }

        private int ParsePort()
        {
            int port;
            int.TryParse(portTextBox.Text.Trim(), out port);
            return port;
        }

        private int ParseHttpsPort()
        {
            if (_httpsPortTextBox == null || string.IsNullOrWhiteSpace(_httpsPortTextBox.Text))
            {
                return 0;
            }
            int port;
            int.TryParse(_httpsPortTextBox.Text.Trim(), out port);
            return port;
        }

        private DeploymentMode GetSelectedDeployMode()
        {
            if (_deployModeCombo == null || _deployModeCombo.SelectedIndex < 0)
            {
                return DeploymentMode.Full;
            }
            return (DeploymentMode)_deployModeCombo.SelectedIndex;
        }

        private static string ValidateUiInput(DeploymentRequest request)
        {
            if (!ValidationHelper.IsSafeName(request.ApplicationName))
            {
                return "Application name is empty or contains invalid characters.";
            }
            if (!ValidationHelper.IsSafeVersion(request.Version))
            {
                return "Version is empty or contains invalid characters.";
            }

            DeploymentMode mode = request.Mode;

            if (DeploymentPlan.NeedsSourceProbe(mode) || DeploymentPlan.RunsCopy(mode))
            {
                if (!ValidationHelper.DirectoryExists(request.SourceFolder))
                {
                    return "Source folder does not exist.";
                }
            }

            if (DeploymentPlan.NeedsIisAvailability(mode))
            {
                if (request.Iis.Mode == IisMode.ApplicationUnderSite)
                {
                    if (!ValidationHelper.IsSafeName(request.Iis.ParentSiteName))
                    {
                        return "Parent IIS site name is empty or contains invalid characters.";
                    }
                    if (!ValidationHelper.IsSafeApplicationPath(request.Iis.ApplicationPath))
                    {
                        return "Application path is invalid. Use a path like /BiCore.";
                    }
                }
                else
                {
                    if (!ValidationHelper.IsSafeName(request.Iis.SiteName))
                    {
                        return "IIS site name is empty or contains invalid characters.";
                    }
                    if (request.Iis.Port < 1 || request.Iis.Port > 65535)
                    {
                        return "Port must be a number between 1 and 65535.";
                    }
                    if (request.Iis.HttpsPort != 0)
                    {
                        if (request.Iis.HttpsPort < 1 || request.Iis.HttpsPort > 65535)
                        {
                            return "HTTPS port must be between 1 and 65535, or leave blank for HTTP only.";
                        }
                        if (request.Iis.HttpsPort == request.Iis.Port)
                        {
                            return "HTTPS port must differ from the HTTP port.";
                        }
                        if (string.IsNullOrWhiteSpace(request.Iis.HttpsCertificateThumbprint))
                        {
                            return "HTTPS port is set â€” select a certificate from LocalMachine\\My.";
                        }
                    }
                }

                if (!ValidationHelper.IsSafeName(request.Iis.ApplicationPoolName))
                {
                    return "Application pool name is empty or contains invalid characters.";
                }
            }

            if (DeploymentPlan.RunsCopy(mode) || DeploymentPlan.RunsIisConfigure(mode)
                || mode == DeploymentMode.RecycleOnly)
            {
                if (string.IsNullOrWhiteSpace(request.DeploymentRoot) || !Path.IsPathRooted(request.DeploymentRoot))
                {
                    return "Deployment location must be an absolute path.";
                }
            }

            if (DeploymentPlan.WillRestoreDatabase(request)
                || (mode == DeploymentMode.DatabaseOnly && request.Database != null
                    && request.Database.Mode == DatabaseMode.RestoreFromBak))
            {
                if (!ValidationHelper.IsSafeName(request.Database.DatabaseName))
                {
                    return "Database name is empty or contains invalid characters.";
                }
                if (!ValidationHelper.FileExists(request.Database.BackupFilePath))
                {
                    return "Backup file does not exist.";
                }
            }

            if (mode == DeploymentMode.DatabaseOnly
                && (request.Database == null || request.Database.Mode != DatabaseMode.RestoreFromBak))
            {
                return "Database only mode requires Restore database from .bak.";
            }

            return null;
        }

        private async Task RunDeploymentAsync(DeploymentRequest request)
        {
            _isDeploying = true;
            _cancellationTokenSource = new CancellationTokenSource();
            SetControlsEnabled(false);
            SetStatus("Deployment started...", true);
            Log.Information("Deployment run {RunId} accepted by operator.", request.RunId);

            try
            {
                DeploymentResult result = await _deploymentService.DeployAsync(
                    request, _cancellationTokenSource.Token);

                ShowResult(request, result);
            }
            catch (Exception ex)
            {
                // Defensive: the service already catches its own errors.
                Log.Error(ex, "Unexpected UI error during deployment.");
                SetStatus("Unexpected error: " + ex.Message, false);
            }
            finally
            {
                _isDeploying = false;
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                SetControlsEnabled(true);
            }
        }

        private void ShowResult(DeploymentRequest request, DeploymentResult result)
        {
            if (result.Success)
            {
                if (!string.IsNullOrEmpty(result.DryRunSummary))
                {
                    SetStatus("Dry-run completed. No changes were made.", false);
                }
                else
                {
                    SetStatus(
                        result.Health == HealthCheckOutcome.Healthy
                            ? "Deployment completed. " + result.HealthCheckSummary
                            : "DEPLOYMENT COMPLETED â€” Health check: FAILED (warning). " + result.HealthCheckSummary,
                        false);
                }
            }
            else if (result.FailedAt == DeploymentStep.HealthCheck)
            {
                SetStatus("FAILED at health check: " + result.ErrorMessage, false);
            }
            else
            {
                SetStatus("FAILED at " + DeploymentResultDialog.DescribeStep(result.FailedAt)
                          + ": " + result.ErrorMessage, false);
            }

            using (var dialog = new DeploymentResultDialog(request, result))
            {
                dialog.ShowDialog(this);
                if (dialog.RollbackRequested)
                {
                    RunRollback(request);
                }
            }
        }

        private void RollbackButton_Click(object sender, EventArgs e)
        {
            DeploymentRequest request = BuildRequestForIisActions();
            if (request == null)
            {
                return;
            }
            RunRollback(request);
        }

        private void RecentButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new RecentDeploymentsDialog())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedForFill == null)
                {
                    return;
                }

                ApplyHistoryEntry(dialog.SelectedForFill);
            }
        }

        /// <summary>
        /// Builds a request for rollback / IIS-only actions. Does not require a source folder.
        /// </summary>
        private DeploymentRequest BuildRequestForIisActions()
        {
            bool appUnderSite = _appUnderSiteRadio.Checked;
            var request = new DeploymentRequest
            {
                ApplicationName = applicationNameCombo.Text.Trim(),
                Version = versionTextBox.Text.Trim(),
                SourceFolder = sourceFolderTextBox.Text.Trim(),
                DeploymentRoot = deploymentRootTextBox.Text.Trim(),
                Iis = new IisSettings
                {
                    Mode = appUnderSite ? IisMode.ApplicationUnderSite : IisMode.SeparateSite,
                    ParentSiteName = _parentSiteCombo.Text.Trim(),
                    ApplicationPath = _applicationPathTextBox.Text.Trim(),
                    SiteName = siteNameTextBox.Text.Trim(),
                    ApplicationPoolName = poolNameTextBox.Text.Trim(),
                    Port = ParsePort(),
                    DeploymentRoot = deploymentRootTextBox.Text.Trim()
                },
                Database = new DatabaseSettings
                {
                    Server = sqlServerTextBox.Text.Trim(),
                    DatabaseName = databaseNameTextBox.Text.Trim(),
                    Mode = DatabaseMode.UseExisting
                },
                RunId = Guid.NewGuid().ToString("N").Substring(0, 8)
            };

            if (!ValidationHelper.IsSafeName(request.ApplicationName))
            {
                KryptonMessageBox.Show(this, "Application name is empty or contains invalid characters.",
                    "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                return null;
            }
            if (string.IsNullOrWhiteSpace(request.DeploymentRoot) || !Path.IsPathRooted(request.DeploymentRoot))
            {
                KryptonMessageBox.Show(this, "Deployment location must be an absolute path.",
                    "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                return null;
            }

            if (request.Iis.Mode == IisMode.ApplicationUnderSite)
            {
                if (!ValidationHelper.IsSafeName(request.Iis.ParentSiteName))
                {
                    KryptonMessageBox.Show(this, "Parent IIS site name is empty or contains invalid characters.",
                        "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                    return null;
                }
                if (!ValidationHelper.IsSafeApplicationPath(request.Iis.ApplicationPath))
                {
                    KryptonMessageBox.Show(this, "Application path is invalid.",
                        "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                    return null;
                }
            }
            else
            {
                if (!ValidationHelper.IsSafeName(request.Iis.SiteName))
                {
                    KryptonMessageBox.Show(this, "IIS site name is empty or contains invalid characters.",
                        "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                    return null;
                }
            }

            if (!ValidationHelper.IsSafeName(request.Iis.ApplicationPoolName))
            {
                KryptonMessageBox.Show(this, "Application pool name is empty or contains invalid characters.",
                    "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                return null;
            }

            return request;
        }

        private void RunRollback(DeploymentRequest request)
        {
            if (request == null)
            {
                return;
            }

            string livePath = null;
            try
            {
                SetStatus("Reading current IIS physical path...", true);
                livePath = _iisService.GetCurrentPhysicalPath(request);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not read current IIS physical path for rollback.");
                KryptonMessageBox.Show(this,
                    "Could not read the current IIS physical path:\n\n" + ex.Message,
                    "Rollback", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Error);
                SetStatus("Ready", false);
                return;
            }
            finally
            {
                SetStatus("Ready", false);
            }

            using (var dialog = new RollbackVersionDialog(request, _iisService, livePath))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK && dialog.RollbackSucceeded)
                {
                    if (!string.IsNullOrEmpty(dialog.RolledBackVersion))
                    {
                        versionTextBox.Text = dialog.RolledBackVersion;
                    }
                    SetStatus(
                        "Rollback complete: IIS â†’ " + (dialog.RolledBackVersion ?? "?")
                        + (string.IsNullOrEmpty(dialog.RolledBackUrl) ? "" : "  " + dialog.RolledBackUrl),
                        false);
                }
            }
        }

        private void ApplyHistoryEntry(DeployHistoryEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(entry.ApplicationName))
            {
                applicationNameCombo.Text = entry.ApplicationName;
            }
            if (!string.IsNullOrWhiteSpace(entry.Version))
            {
                versionTextBox.Text = entry.Version;
            }

            // Infer deployment root from TargetFolder = Root\App\Version when possible.
            if (!string.IsNullOrWhiteSpace(entry.TargetFolder))
            {
                try
                {
                    string versionDir = entry.TargetFolder.TrimEnd('\\', '/');
                    string appDir = Path.GetDirectoryName(versionDir);
                    if (!string.IsNullOrEmpty(appDir))
                    {
                        string root = Path.GetDirectoryName(appDir);
                        if (!string.IsNullOrEmpty(root) && Path.IsPathRooted(root))
                        {
                            deploymentRootTextBox.Text = root;
                        }
                    }
                }
                catch (Exception)
                {
                    // Leave existing root.
                }
            }

            SetStatus("Loaded from history: " + (entry.ApplicationName ?? "?") + " v" + (entry.Version ?? "?"), false);
        }

        private void OnDeploymentProgress(object sender, ProgressEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }
            try
            {
                BeginInvoke((Action)(() =>
                {
                    if (!IsDisposed)
                    {
                        SetStatus(e.Message, e.IsIndeterminate);
                    }
                }));
            }
            catch (InvalidOperationException)
            {
                // Window closed while deployment was finishing.
            }
        }

        // ------------------------------------------------------------------
        // Status helpers
        // ------------------------------------------------------------------

        private void SetControlsEnabled(bool enabled)
        {
            deployButton.Enabled = enabled;
            if (_validateButton != null)
            {
                _validateButton.Enabled = enabled;
            }
            if (_rollbackButton != null)
            {
                _rollbackButton.Enabled = enabled;
            }
            if (_recentButton != null)
            {
                _recentButton.Enabled = enabled;
            }
            cancelDeployButton.Enabled = !enabled;
            applicationNameCombo.Enabled = enabled;
            versionTextBox.Enabled = enabled;
            if (_deployModeCombo != null)
            {
                _deployModeCombo.Enabled = enabled;
            }
            if (_dryRunCheck != null)
            {
                _dryRunCheck.Enabled = enabled;
            }
            sourceFolderTextBox.Enabled = enabled;
            browseSourceButton.Enabled = enabled;
            useExistingRadio.Enabled = enabled;
            restoreFromBakRadio.Enabled = enabled;
            databaseNameTextBox.Enabled = enabled;
            backupFileTextBox.Enabled = enabled && restoreFromBakRadio.Checked;
            browseBackupButton.Enabled = enabled && restoreFromBakRadio.Checked;
            sqlServerTextBox.Enabled = enabled;
            if (_windowsAuthRadio != null)
            {
                _windowsAuthRadio.Enabled = enabled;
            }
            if (_sqlAuthRadio != null)
            {
                _sqlAuthRadio.Enabled = enabled;
            }
            if (_setSqlCredentialsButton != null)
            {
                _setSqlCredentialsButton.Enabled = enabled && _sqlAuthRadio != null && _sqlAuthRadio.Checked;
            }
            if (_updateConnectionStringCheck != null)
            {
                _updateConnectionStringCheck.Enabled = enabled;
            }
            if (_connectionStringTextBox != null)
            {
                _connectionStringTextBox.Enabled = enabled && _updateConnectionStringCheck != null && _updateConnectionStringCheck.Checked;
            }
            if (_buildConnectionStringButton != null)
            {
                _buildConnectionStringButton.Enabled = enabled && _updateConnectionStringCheck != null && _updateConnectionStringCheck.Checked;
            }
            _appUnderSiteRadio.Enabled = enabled;
            _separateSiteRadio.Enabled = enabled;
            _parentSiteCombo.Enabled = enabled && _appUnderSiteRadio.Checked;
            _applicationPathTextBox.Enabled = enabled && _appUnderSiteRadio.Checked;
            siteNameTextBox.Enabled = enabled && !_appUnderSiteRadio.Checked;
            portTextBox.Enabled = enabled && !_appUnderSiteRadio.Checked;
            if (_httpsPortTextBox != null)
            {
                _httpsPortTextBox.Enabled = enabled && !_appUnderSiteRadio.Checked;
            }
            if (_httpsCertCombo != null)
            {
                _httpsCertCombo.Enabled = enabled && !_appUnderSiteRadio.Checked && ParseHttpsPort() > 0;
            }
            poolNameTextBox.Enabled = enabled;
            deploymentRootTextBox.Enabled = enabled;
            browseRootButton.Enabled = enabled;
            if (_healthCheckPathTextBox != null)
            {
                _healthCheckPathTextBox.Enabled = enabled;
            }
        }
    }
}
