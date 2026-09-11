using System;
using System.Collections.Generic;
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
        private void WireEvents()
        {
            browseSourceButton.Click += BrowseSourceButton_Click;
            browseBackupButton.Click += BrowseBackupButton_Click;
            browseRootButton.Click += BrowseRootButton_Click;
            applicationNameCombo.TextChanged += ApplicationName_TextChanged;
            useExistingRadio.CheckedChanged += DatabaseMode_CheckedChanged;
            restoreFromBakRadio.CheckedChanged += DatabaseMode_CheckedChanged;
            _windowsAuthRadio.CheckedChanged += SqlAuthMode_CheckedChanged;
            _sqlAuthRadio.CheckedChanged += SqlAuthMode_CheckedChanged;
            _setSqlCredentialsButton.Click += SetSqlCredentialsButton_Click;
            _updateConnectionStringCheck.CheckedChanged += UpdateConnectionString_CheckedChanged;
            _buildConnectionStringButton.Click += BuildConnectionStringButton_Click;
            _appUnderSiteRadio.CheckedChanged += IisMode_CheckedChanged;
            _separateSiteRadio.CheckedChanged += IisMode_CheckedChanged;
            portTextBox.TextChanged += PortTextBox_TextChanged;
            portTextBox.Leave += PortTextBox_TextChanged;
            if (_httpsPortTextBox != null)
            {
                _httpsPortTextBox.TextChanged += HttpsPortTextBox_TextChanged;
                _httpsPortTextBox.Leave += HttpsPortTextBox_TextChanged;
            }
            deployButton.Click += DeployButton_Click;
            cancelDeployButton.Click += CancelDeployButton_Click;
            _validateButton.Click += ValidateButton_Click;
            _rollbackButton.Click += RollbackButton_Click;
            _recentButton.Click += RecentButton_Click;
            sourceFolderTextBox.TextChanged += SourceFolder_TextChanged;
        }

        private void LoadDefaults()
        {
            deploymentRootTextBox.Text = ResolveStartingDeploymentRoot();
            sqlServerTextBox.Text = AppConfig.SqlServer;
            applicationNameCombo.Items.AddRange(new object[] { "BiCore", "BiSite" });
            applicationNameCombo.Text = "BiCore";
            versionTextBox.Text = "1.0.0";
            databaseNameTextBox.Text = "BiCore";
            poolNameTextBox.Text = "BiCore";
            _applicationPathTextBox.Text = "/BiCore";
            siteNameTextBox.Text = "BiCore";
            portTextBox.Text = "8099";
            if (_httpsPortTextBox != null)
            {
                _httpsPortTextBox.Text = string.Empty;
            }
            if (_healthCheckPathTextBox != null)
            {
                _healthCheckPathTextBox.Text = AppConfig.HealthCheckPath;
            }
            LoadHttpsCertificates();
            UpdateDatabaseFieldState();
            UpdateIisModeFieldState();
            UpdateSqlAuthFieldState();
            UpdateConnectionStringFieldState();
            UpdateHttpsFieldState();
            RefreshPortSoftWarn();
            LoadParentSites();
        }

        /// <summary>
        /// Starting value for the deployment location: the remembered root when it is a
        /// valid absolute path, otherwise the App.config default.
        /// </summary>
        private static string ResolveStartingDeploymentRoot()
        {
            string remembered = RecentPathsStore.Get(RecentKeyDeploymentRoot);
            if (!string.IsNullOrWhiteSpace(remembered) && Path.IsPathRooted(remembered))
            {
                return remembered;
            }
            return AppConfig.DeploymentRoot;
        }

        private void LoadParentSites()
        {
            string previous = _parentSiteCombo.Text;
            Task.Run(() =>
            {
                try
                {
                    return _iisService.ListSiteNames();
                }
                catch
                {
                    return new string[0];
                }
            })
            .ContinueWith(t =>
            {
                if (IsDisposed)
                {
                    return;
                }
                _parentSiteCombo.Items.Clear();
                foreach (string name in t.Result)
                {
                    _parentSiteCombo.Items.Add(name);
                }
                if (_parentSiteCombo.Items.Contains(previous))
                {
                    _parentSiteCombo.Text = previous;
                }
                else if (_parentSiteCombo.Items.Contains("Default Web Site"))
                {
                    _parentSiteCombo.SelectedIndex = _parentSiteCombo.Items.IndexOf("Default Web Site");
                }
                else if (_parentSiteCombo.Items.Count > 0)
                {
                    _parentSiteCombo.SelectedIndex = 0;
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void CheckElevation()
        {
            if (ElevationHelper.IsElevated)
            {
                Log.Information("Running elevated; IIS management available.");
                return;
            }

            Log.Warning("IntraDeploy started without administrator privileges.");

            DialogResult choice = KryptonMessageBox.Show(
                this,
                "IIS management requires administrator privileges.\n\n" +
                "Restart IntraDeploy as administrator now?",
                "Administrator privileges required",
                KryptonMessageBoxButtons.YesNo,
                KryptonMessageBoxIcon.Warning,
                KryptonMessageBoxDefaultButton.Button1);

            if (choice == DialogResult.Yes)
            {
                if (ElevationHelper.RestartElevated())
                {
                    Close();
                    return;
                }
            }

            SetStatus("Not elevated: IIS operations will fail until restarted as administrator.", false);
        }

        // ------------------------------------------------------------------
        // Event handlers
        // ------------------------------------------------------------------

        private void SourceFolder_TextChanged(object sender, EventArgs e)
        {
            // Fire and forget detection; never blocks typing.
            string folder = sourceFolderTextBox.Text.Trim();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                detectedTypeLabel.Text = string.IsNullOrEmpty(folder) ? " " : "Folder does not exist";
                return;
            }

            Task.Run(() => _applicationDetector.Probe(folder))
                .ContinueWith(t =>
                {
                    if (IsDisposed)
                    {
                        return;
                    }
                    ApplicationProbeResult probe = t.Result;
                    detectedTypeLabel.Text = probe.IsValid
                        ? "Detected: " + probe.Info.Description
                        : probe.ErrorMessage;
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void BrowseSourceButton_Click(object sender, EventArgs e)
        {
            string selected = ExplorerFolderPicker.PickFolder(
                this,
                "Select the published application folder (Publish > Folder output)",
                ResolveInitialFolder(sourceFolderTextBox.Text, RecentPathsStore.Get(RecentKeySourceFolder)));
            if (!string.IsNullOrEmpty(selected))
            {
                sourceFolderTextBox.Text = selected;
                RecentPathsStore.Set(RecentKeySourceFolder, selected);
            }
        }

        private void BrowseBackupButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Select the SQL Server backup file (.bak) to restore";
                dialog.Filter = "SQL Server backup files (*.bak)|*.bak|All files (*.*)|*.*";
                dialog.FilterIndex = 1;
                dialog.CheckFileExists = true;
                dialog.CheckPathExists = true;
                dialog.Multiselect = false;
                dialog.RestoreDirectory = true;
                dialog.InitialDirectory = ResolveInitialFolder(
                    Path.GetDirectoryName(backupFileTextBox.Text), RecentPathsStore.Get(RecentKeyBackupFolder));
                if (File.Exists(backupFileTextBox.Text))
                {
                    dialog.FileName = Path.GetFileName(backupFileTextBox.Text);
                }
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    backupFileTextBox.Text = dialog.FileName;
                    string folder = Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(folder))
                    {
                        RecentPathsStore.Set(RecentKeyBackupFolder, folder);
                    }
                }
            }
        }

        private void BrowseRootButton_Click(object sender, EventArgs e)
        {
            string selected = ExplorerFolderPicker.PickFolder(
                this,
                "Select the deployment root directory",
                ResolveInitialFolder(deploymentRootTextBox.Text, AppConfig.DeploymentRoot));
            if (!string.IsNullOrEmpty(selected))
            {
                deploymentRootTextBox.Text = selected;
                RecentPathsStore.Set(RecentKeyDeploymentRoot, selected);
            }
        }

        /// <summary>
        /// Best starting directory for a browse dialog: the current value when it exists on
        /// disk, then the remembered last-used location, then null (let Windows decide).
        /// </summary>
        private static string ResolveInitialFolder(string currentValue, string rememberedValue)
        {
            if (!string.IsNullOrWhiteSpace(currentValue))
            {
                try
                {
                    if (Directory.Exists(currentValue))
                    {
                        return currentValue;
                    }
                }
                catch (ArgumentException) { }
            }
            if (!string.IsNullOrWhiteSpace(rememberedValue) && Directory.Exists(rememberedValue))
            {
                return rememberedValue;
            }
            return null;
        }

        private void ApplicationName_TextChanged(object sender, EventArgs e)
        {
            string name = applicationNameCombo.Text.Trim();
            poolNameTextBox.Text = name;
            _applicationPathTextBox.Text = "/" + name;
            siteNameTextBox.Text = name;
            databaseNameTextBox.Text = name;
        }

        private void DatabaseMode_CheckedChanged(object sender, EventArgs e)
        {
            UpdateDatabaseFieldState();
        }

        private void SqlAuthMode_CheckedChanged(object sender, EventArgs e)
        {
            var radio = sender as KryptonRadioButton;
            if (radio == null || !radio.Checked)
            {
                return;
            }

            if (radio == _windowsAuthRadio)
            {
                _sessionSqlUser = null;
                _sessionSqlPassword = null;
            }
            else if (radio == _sqlAuthRadio && string.IsNullOrWhiteSpace(_sessionSqlUser))
            {
                PromptSqlCredentials();
            }
            UpdateSqlAuthFieldState();
        }

        private void SetSqlCredentialsButton_Click(object sender, EventArgs e)
        {
            PromptSqlCredentials();
        }

        private void PromptSqlCredentials()
        {
            using (var dialog = new SqlCredentialsDialog(_sessionSqlUser))
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _sessionSqlUser = dialog.SqlUser;
                    _sessionSqlPassword = dialog.SqlPassword;
                    Log.Information("SQL login credentials set for this session (user={User}); password not logged.",
                        _sessionSqlUser);
                }
                else if (string.IsNullOrWhiteSpace(_sessionSqlUser))
                {
                    // Operator cancelled without credentials â€” fall back to Windows auth.
                    _windowsAuthRadio.Checked = true;
                }
            }
            UpdateSqlAuthFieldState();
        }

        private void UpdateConnectionString_CheckedChanged(object sender, EventArgs e)
        {
            UpdateConnectionStringFieldState();
            if (_updateConnectionStringCheck.Checked && string.IsNullOrWhiteSpace(_connectionStringTextBox.Text))
            {
                TryBuildConnectionStringIntoBox();
            }
        }

        private void BuildConnectionStringButton_Click(object sender, EventArgs e)
        {
            TryBuildConnectionStringIntoBox();
        }

        private void TryBuildConnectionStringIntoBox()
        {
            if (string.IsNullOrWhiteSpace(sqlServerTextBox.Text) || string.IsNullOrWhiteSpace(databaseNameTextBox.Text))
            {
                KryptonMessageBox.Show(this,
                    "Enter SQL Server and Database Name before building a connection string.",
                    "Connection string",
                    KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Information);
                return;
            }

            if (_sqlAuthRadio.Checked && string.IsNullOrWhiteSpace(_sessionSqlUser))
            {
                PromptSqlCredentials();
                if (string.IsNullOrWhiteSpace(_sessionSqlUser))
                {
                    return;
                }
            }

            var settings = new DatabaseSettings
            {
                Server = sqlServerTextBox.Text.Trim(),
                DatabaseName = databaseNameTextBox.Text.Trim(),
                SqlUser = _sqlAuthRadio.Checked ? _sessionSqlUser : null,
                SqlPassword = _sqlAuthRadio.Checked ? _sessionSqlPassword : null
            };
            _connectionStringTextBox.Text = DatabaseService.BuildApplicationConnectionString(settings);
        }

        private void IisMode_CheckedChanged(object sender, EventArgs e)
        {
            UpdateIisModeFieldState();
            RefreshPortSoftWarn();
        }

        private void PortTextBox_TextChanged(object sender, EventArgs e)
        {
            RefreshPortSoftWarn();
        }

        private void UpdateDatabaseFieldState()
        {
            bool restoreMode = restoreFromBakRadio.Checked;
            backupFileTextBox.Enabled = restoreMode;
            browseBackupButton.Enabled = restoreMode;
        }

        private void UpdateSqlAuthFieldState()
        {
            bool sqlAuth = _sqlAuthRadio != null && _sqlAuthRadio.Checked;
            if (_setSqlCredentialsButton != null)
            {
                _setSqlCredentialsButton.Enabled = sqlAuth && !_isDeploying;
            }
        }

        private void UpdateConnectionStringFieldState()
        {
            bool enabled = _updateConnectionStringCheck != null && _updateConnectionStringCheck.Checked && !_isDeploying;
            if (_connectionStringTextBox != null)
            {
                _connectionStringTextBox.Enabled = enabled;
            }
            if (_buildConnectionStringButton != null)
            {
                _buildConnectionStringButton.Enabled = enabled;
            }
        }

        private void UpdateIisModeFieldState()
        {
            bool appUnderSite = _appUnderSiteRadio.Checked;
            _parentSiteCombo.Enabled = appUnderSite;
            _applicationPathTextBox.Enabled = appUnderSite;
            siteNameTextBox.Enabled = !appUnderSite;
            portTextBox.Enabled = !appUnderSite;
            if (_portSoftWarnLabel != null)
            {
                _portSoftWarnLabel.Visible = !appUnderSite;
            }
            UpdateHttpsFieldState();
        }

        private void UpdateHttpsFieldState()
        {
            bool separate = _separateSiteRadio != null && _separateSiteRadio.Checked && !_isDeploying;
            if (_httpsPortTextBox != null)
            {
                _httpsPortTextBox.Enabled = separate;
            }
            if (_httpsCertCombo != null)
            {
                bool httpsRequested = separate && ParseHttpsPort() > 0;
                _httpsCertCombo.Enabled = httpsRequested;
            }
        }

        private void HttpsPortTextBox_TextChanged(object sender, EventArgs e)
        {
            UpdateHttpsFieldState();
        }

        private void LoadHttpsCertificates()
        {
            if (_httpsCertCombo == null)
            {
                return;
            }

            string previous = GetSelectedHttpsThumbprint();
            _httpsCertCombo.Items.Clear();
            _httpsCertCombo.Items.Add("(none â€” HTTP only)");
            try
            {
                IList<CertificateInfo> certs = _iisService.ListLocalMachineCertificates("My");
                foreach (CertificateInfo cert in certs)
                {
                    _httpsCertCombo.Items.Add(cert);
                }
            }
            catch
            {
                // Combo stays with "(none)".
            }

            int select = 0;
            if (!string.IsNullOrEmpty(previous))
            {
                for (int i = 1; i < _httpsCertCombo.Items.Count; i++)
                {
                    CertificateInfo item = _httpsCertCombo.Items[i] as CertificateInfo;
                    if (item != null
                        && string.Equals(item.Thumbprint, previous, StringComparison.OrdinalIgnoreCase))
                    {
                        select = i;
                        break;
                    }
                }
            }
            _httpsCertCombo.SelectedIndex = select;
        }

        private string GetSelectedHttpsThumbprint()
        {
            if (_httpsCertCombo == null || _httpsCertCombo.SelectedItem == null)
            {
                return null;
            }
            CertificateInfo cert = _httpsCertCombo.SelectedItem as CertificateInfo;
            return cert != null ? cert.Thumbprint : null;
        }

        /// <summary>
        /// Soft warning when SeparateSite port already has a local TCP listener (PortHelper).
        /// Does not block deploy; GetPortConflict still runs as the hard IIS binding check.
        /// </summary>
        private void RefreshPortSoftWarn()
        {
            if (_portSoftWarnLabel == null)
            {
                return;
            }

            if (_appUnderSiteRadio == null || _appUnderSiteRadio.Checked)
            {
                _portSoftWarnLabel.Text = " ";
                return;
            }

            int port;
            if (!int.TryParse(portTextBox.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                _portSoftWarnLabel.Text = " ";
                return;
            }

            try
            {
                _portSoftWarnLabel.Text = PortHelper.IsPortInUse(port)
                    ? "Soft warn: TCP port " + port + " appears in use on this machine."
                    : " ";
            }
            catch
            {
                _portSoftWarnLabel.Text = " ";
            }
        }
    }
}
