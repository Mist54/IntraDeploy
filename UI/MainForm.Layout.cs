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
        private void BuildLayout()
        {
            _tooltip = new ToolTip
            {
                InitialDelay = 400,
                ReshowDelay = 150,
                AutoPopDelay = 15000
            };

            // Status bar at the bottom (thin; keeps SetStatus working).
            _statusLabel = new ToolStripStatusLabel { Text = "Ready" };
            _statusBar = new KryptonStatusStrip();
            _statusBar.Items.Add(_statusLabel);
            Controls.Add(_statusBar);
            _statusBar.BringToFront();

            // Title lives in the Windows caption; keep header controls but do not show them.
            headerLabel.Visible = false;
            headerSubtitle.Visible = false;

            var rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoScroll = true,
                Padding = new Padding(14, 12, 14, 8)
            };
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 4; i++)
            {
                rootLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            StyleSectionGroup(applicationGroup);
            StyleSectionGroup(databaseGroup);
            StyleSectionGroup(iisGroup);

            // --- Application -----------------------------------------------------------
            var appPanel = CreateFieldPanel();
            applicationLabel.Text = "Application";
            AddField(appPanel, 0, applicationLabel, applicationNameCombo, null);
            versionLabel.Text = "Version";
            AddField(appPanel, 1, versionLabel, versionTextBox, null);
            sourceFolderLabel.Text = "Published Folder";
            browseSourceButton.Text = "Browse...";
            AddField(appPanel, 2, sourceFolderLabel, sourceFolderTextBox, browseSourceButton);
            detectedTypeLabel.Text = " ";
            detectedTypeLabel.LabelStyle = LabelStyle.NormalControl;
            detectedTypeLabel.AutoSize = true;
            detectedTypeLabel.Margin = new Padding(3, 0, 3, 2);
            appPanel.Controls.Add(detectedTypeLabel, 1, 3);
            appPanel.SetColumnSpan(detectedTypeLabel, 2);

            var modeLabel = new KryptonLabel { Text = "Deploy mode", AutoSize = true };
            _deployModeCombo = new KryptonComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill
            };
            _deployModeCombo.Items.Add(DeploymentPlan.DescribeMode(DeploymentMode.Full));
            _deployModeCombo.Items.Add(DeploymentPlan.DescribeMode(DeploymentMode.FilesAndIis));
            _deployModeCombo.Items.Add(DeploymentPlan.DescribeMode(DeploymentMode.IisOnly));
            _deployModeCombo.Items.Add(DeploymentPlan.DescribeMode(DeploymentMode.DatabaseOnly));
            _deployModeCombo.Items.Add(DeploymentPlan.DescribeMode(DeploymentMode.RecycleOnly));
            _deployModeCombo.SelectedIndex = 0;
            AddField(appPanel, 4, modeLabel, _deployModeCombo, null);

            _dryRunCheck = new KryptonCheckBox
            {
                Text = "Dry-run (report plan, no writes)",
                AutoSize = true,
                Checked = false,
                Margin = new Padding(0, 4, 0, 2)
            };
            appPanel.Controls.Add(_dryRunCheck, 0, 5);
            appPanel.SetColumnSpan(_dryRunCheck, 3);

            applicationGroup.Panel.Controls.Add(appPanel);
            applicationGroup.Margin = new Padding(0, 0, 0, 10);

            // --- Database --------------------------------------------------------------
            var dbPanel = CreateFieldPanel();
            useExistingRadio.Text = "Use existing database";
            useExistingRadio.AutoSize = true;
            useExistingRadio.Checked = true;
            useExistingRadio.Margin = new Padding(0, 2, 16, 2);
            restoreFromBakRadio.Text = "Restore database from .bak";
            restoreFromBakRadio.AutoSize = true;
            restoreFromBakRadio.Margin = new Padding(0, 2, 0, 2);
            AddRadioRow(dbPanel, 0, useExistingRadio, restoreFromBakRadio);

            databaseNameLabel.Text = "Database Name";
            AddField(dbPanel, 1, databaseNameLabel, databaseNameTextBox, null);
            backupFileLabel.Text = "Backup File (.bak)";
            browseBackupButton.Text = "Browse...";
            AddField(dbPanel, 2, backupFileLabel, backupFileTextBox, browseBackupButton);
            sqlServerLabel.Text = "SQL Server";
            AddField(dbPanel, 3, sqlServerLabel, sqlServerTextBox, null);

            _windowsAuthRadio = new KryptonRadioButton
            {
                Text = "Windows authentication",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 2, 16, 2)
            };
            _sqlAuthRadio = new KryptonRadioButton
            {
                Text = "SQL Server login",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 2)
            };
            AddRadioRow(dbPanel, 4, _windowsAuthRadio, _sqlAuthRadio);

            var authLabel = new KryptonLabel { Text = "SQL credentials", AutoSize = true };
            _setSqlCredentialsButton = new KryptonButton
            {
                Text = "Set login...",
                AutoSize = false,
                Size = new Size(88, 28),
                Enabled = false
            };
            var authHint = new KryptonLabel
            {
                Text = "Session only â€” never saved",
                AutoSize = true,
                LabelStyle = LabelStyle.NormalControl,
                Dock = DockStyle.Fill
            };
            AddField(dbPanel, 5, authLabel, authHint, _setSqlCredentialsButton);

            _updateConnectionStringCheck = new KryptonCheckBox
            {
                Text = "Update connection string in deployed config",
                AutoSize = true,
                Checked = false,
                Margin = new Padding(0, 4, 0, 2)
            };
            dbPanel.Controls.Add(_updateConnectionStringCheck, 0, 6);
            dbPanel.SetColumnSpan(_updateConnectionStringCheck, 3);

            var connLabel = new KryptonLabel { Text = "Connection string", AutoSize = true };
            _connectionStringTextBox = new KryptonTextBox { Dock = DockStyle.Fill, Enabled = false };
            _buildConnectionStringButton = new KryptonButton
            {
                Text = "Build...",
                AutoSize = false,
                Size = new Size(88, 28),
                Enabled = false
            };
            AddField(dbPanel, 7, connLabel, _connectionStringTextBox, _buildConnectionStringButton);

            databaseGroup.Panel.Controls.Add(dbPanel);
            databaseGroup.Margin = new Padding(0, 0, 0, 10);

            // --- IIS -------------------------------------------------------------------
            var iisPanel = CreateFieldPanel();

            _appUnderSiteRadio = new KryptonRadioButton
            {
                Text = "Application under existing IIS Site",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 2, 16, 2)
            };
            _separateSiteRadio = new KryptonRadioButton
            {
                Text = "Separate IIS Site",
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 2)
            };
            AddRadioRow(iisPanel, 0, _appUnderSiteRadio, _separateSiteRadio);

            siteNameLabel.Text = "Parent IIS Site";
            _parentSiteCombo = new KryptonComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            AddField(iisPanel, 1, siteNameLabel, _parentSiteCombo, null);

            poolNameLabel.Text = "Application Path";
            _applicationPathTextBox = new KryptonTextBox { Dock = DockStyle.Fill };
            AddField(iisPanel, 2, poolNameLabel, _applicationPathTextBox, null);

            // Reuse Designer labels/textboxes (same names; no anonymous replacements).
            var iisSiteNameLabel = new KryptonLabel { Text = "IIS Site Name", AutoSize = true };
            AddField(iisPanel, 3, iisSiteNameLabel, siteNameTextBox, null);

            portLabel.Text = "Port";
            AddField(iisPanel, 4, portLabel, portTextBox, null);

            _portSoftWarnLabel = new KryptonLabel
            {
                Text = " ",
                LabelStyle = LabelStyle.NormalControl,
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 2)
            };
            iisPanel.Controls.Add(_portSoftWarnLabel, 1, 5);
            iisPanel.SetColumnSpan(_portSoftWarnLabel, 2);

            var httpsPortLabel = new KryptonLabel { Text = "HTTPS port (optional)", AutoSize = true };
            _httpsPortTextBox = new KryptonTextBox { Dock = DockStyle.Fill };
            AddField(iisPanel, 6, httpsPortLabel, _httpsPortTextBox, null);

            var httpsCertLabel = new KryptonLabel { Text = "HTTPS certificate", AutoSize = true };
            _httpsCertCombo = new KryptonComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
            AddField(iisPanel, 7, httpsCertLabel, _httpsCertCombo, null);

            var appPoolLabel = new KryptonLabel { Text = "Application Pool", AutoSize = true };
            AddField(iisPanel, 8, appPoolLabel, poolNameTextBox, null);

            deploymentRootLabel.Text = "Deployment Location";
            browseRootButton.Text = "Browse...";
            AddField(iisPanel, 9, deploymentRootLabel, deploymentRootTextBox, browseRootButton);

            var healthPathLabel = new KryptonLabel { Text = "Health check path", AutoSize = true };
            _healthCheckPathTextBox = new KryptonTextBox { Dock = DockStyle.Fill };
            AddField(iisPanel, 10, healthPathLabel, _healthCheckPathTextBox, null);

            iisGroup.Panel.Controls.Add(iisPanel);
            iisGroup.Margin = new Padding(0, 0, 0, 8);

            rootLayout.Controls.Add(applicationGroup, 0, 0);
            rootLayout.Controls.Add(databaseGroup, 0, 1);
            rootLayout.Controls.Add(iisGroup, 0, 2);

            // --- Footer: Recent + Rollback + Validate + Cancel + DEPLOY --------------
            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true,
                AutoSize = true,
                Padding = new Padding(0, 6, 0, 2),
                Margin = new Padding(0)
            };

            StylePrimaryDeployButton(deployButton);
            StyleSecondaryCancelButton(cancelDeployButton);
            _validateButton = new KryptonButton();
            StyleSecondaryValidateButton(_validateButton);
            _rollbackButton = new KryptonButton();
            StyleSecondaryValidateButton(_rollbackButton);
            _rollbackButton.Text = "Rollback...";
            _rollbackButton.Size = new Size(110, 36);
            _recentButton = new KryptonButton();
            StyleSecondaryValidateButton(_recentButton);
            _recentButton.Text = "Recent...";
            _recentButton.Size = new Size(100, 36);
            SetTooltip(deployButton, "Runs the selected deploy mode after the confirmation dialog. With Dry-run checked, only reports the plan.");
            SetTooltip(cancelDeployButton, "Cancels the running deployment after the current step.");
            SetTooltip(_validateButton, "Runs read-only pre-flight checks (no file, IIS, or SQL writes).");
            SetTooltip(_rollbackButton, "Point IIS at a prior version folder under the app root and recycle. Does not delete the current folder.");
            SetTooltip(_recentButton, "Recent deployments from local history (no secrets). Fill the form or open folder/URL.");
            SetTooltip(_deployModeCombo, "Full or partial pipeline: Files+IIS, IIS only, Database only, or Recycle only. Confirm dialog lists steps that will run.");
            SetTooltip(_dryRunCheck, "After pre-flight, report planned target folder, IIS, database, and config actions â€” then exit with no writes.");

            buttonRow.Controls.Add(deployButton);
            buttonRow.Controls.Add(cancelDeployButton);
            buttonRow.Controls.Add(_validateButton);
            buttonRow.Controls.Add(_rollbackButton);
            buttonRow.Controls.Add(_recentButton);
            rootLayout.Controls.Add(buttonRow, 0, 3);

            rootPanel.Controls.Add(rootLayout);

            SetTooltip(applicationNameCombo, "Logical application name, e.g. BiCore. Used for the deployment sub-folder and IIS defaults.");
            SetTooltip(versionTextBox, "Application version, e.g. 2.5.0. Deployed into its own version folder.");
            SetTooltip(sourceFolderTextBox, "Full path of the Visual Studio published application folder (Publish as Folder). Hover to read the complete path.");
            SetTooltip(browseSourceButton, "Select the published application folder.");
            SetTooltip(databaseNameTextBox, "Name of the SQL Server database this application uses.");
            SetTooltip(backupFileTextBox, "SQL Server .bak file. Only used in restore mode. Hover to read the complete path.");
            SetTooltip(browseBackupButton, "Select the .bak file to restore.");
            SetTooltip(sqlServerTextBox, "SQL Server instance, e.g. localhost or SERVER\\SQLEXPRESS.");
            SetTooltip(_windowsAuthRadio, "Connect to SQL Server with the Windows account running IntraDeploy (default).");
            SetTooltip(_sqlAuthRadio, "Connect with a SQL login. Credentials stay in memory for this session only and are never saved.");
            SetTooltip(_setSqlCredentialsButton, "Enter SQL user and password for this session. Never written to disk.");
            SetTooltip(_updateConnectionStringCheck, "When checked, IntraDeploy updates the first connection string in the deployed web.config / appsettings.json only.");
            SetTooltip(_connectionStringTextBox, "Connection string written to the deployed copy. Never logged. Prefer Build... from SQL Server + Database Name.");
            SetTooltip(_buildConnectionStringButton, "Build a connection string from SQL Server, Database Name, and the current auth mode.");
            SetTooltip(_parentSiteCombo, "Existing IIS site that will host the application, e.g. Default Web Site.");
            SetTooltip(_applicationPathTextBox, "Application path under the parent site, e.g. /BiCore for http://localhost/BiCore.");
            SetTooltip(siteNameTextBox, "Name for a new separate IIS site.");
            SetTooltip(portTextBox, "HTTP port for the separate site binding, e.g. 8099. HTTPS is optional below.");
            SetTooltip(_httpsPortTextBox, "Optional HTTPS port for Separate Site (e.g. 8443). Leave blank for HTTP only. Requires a LocalMachine\\My certificate.");
            SetTooltip(_httpsCertCombo, "Certificate from LocalMachine\\My (private key required). Used only when HTTPS port is set.");
            SetTooltip(poolNameTextBox, "IIS application pool. Created if missing; reused (never modified) if it exists. CLR mismatch requires an explicit Continue on Deploy.");
            SetTooltip(deploymentRootTextBox, "Root directory that receives versioned deployment folders, e.g. C:\\IntraDeploy\\Applications\\BiCore\\2.5.0. Hover to read the complete path.");
            SetTooltip(browseRootButton, "Select the deployment root directory.");
            SetTooltip(_healthCheckPathTextBox, "Optional path appended to the site/app URL for the post-deploy health check (e.g. / or /health). Leave blank to use App.config HealthCheckPath.");
        }

        private static void StyleSectionGroup(KryptonGroupBox group)
        {
            group.AutoSize = true;
            group.Dock = DockStyle.Fill;
            group.CaptionStyle = LabelStyle.TitleControl;
            group.GroupBorderStyle = PaletteBorderStyle.ControlClient;
            group.GroupBackStyle = PaletteBackStyle.ControlClient;
            group.StateCommon.Content.ShortText.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            group.StateCommon.Content.ShortText.Color1 = Color.FromArgb(0, 99, 177);
        }

        private static void StylePrimaryDeployButton(KryptonButton button)
        {
            button.Text = "DEPLOY";
            button.AutoSize = false;
            button.Size = new Size(110, 32);
            button.Margin = new Padding(10, 0, 0, 0);
            button.StateCommon.Back.Color1 = Color.FromArgb(0, 120, 212);
            button.StateCommon.Back.Color2 = Color.FromArgb(0, 120, 212);
            button.StateCommon.Border.Color1 = Color.FromArgb(0, 99, 177);
            button.StateCommon.Border.Color2 = Color.FromArgb(0, 99, 177);
            button.StateCommon.Border.DrawBorders = PaletteDrawBorders.All;
            button.StateCommon.Border.Rounding = 3;
            button.StateCommon.Content.ShortText.Color1 = Color.White;
            button.StateCommon.Content.ShortText.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            button.OverrideDefault.Back.Color1 = Color.FromArgb(0, 120, 212);
            button.OverrideDefault.Back.Color2 = Color.FromArgb(0, 120, 212);
            button.OverrideDefault.Content.ShortText.Color1 = Color.White;
        }

        private static void StyleSecondaryCancelButton(KryptonButton button)
        {
            button.Text = "Cancel";
            button.AutoSize = false;
            button.Size = new Size(90, 32);
            button.Margin = new Padding(0);
            button.StateCommon.Back.Color1 = Color.FromArgb(245, 245, 245);
            button.StateCommon.Back.Color2 = Color.FromArgb(245, 245, 245);
            button.StateCommon.Border.Color1 = Color.FromArgb(180, 180, 180);
            button.StateCommon.Border.Color2 = Color.FromArgb(180, 180, 180);
            button.StateCommon.Border.DrawBorders = PaletteDrawBorders.All;
            button.StateCommon.Border.Rounding = 3;
            button.StateCommon.Content.ShortText.Color1 = Color.FromArgb(50, 50, 50);
            button.StateCommon.Content.ShortText.Font = new Font("Segoe UI", 9f);
        }

        private static void StyleSecondaryValidateButton(KryptonButton button)
        {
            button.Text = "Validate";
            button.AutoSize = false;
            button.Size = new Size(90, 32);
            button.Margin = new Padding(0, 0, 10, 0);
            button.StateCommon.Back.Color1 = Color.FromArgb(245, 245, 245);
            button.StateCommon.Back.Color2 = Color.FromArgb(245, 245, 245);
            button.StateCommon.Border.Color1 = Color.FromArgb(180, 180, 180);
            button.StateCommon.Border.Color2 = Color.FromArgb(180, 180, 180);
            button.StateCommon.Border.DrawBorders = PaletteDrawBorders.All;
            button.StateCommon.Border.Rounding = 3;
            button.StateCommon.Content.ShortText.Color1 = Color.FromArgb(50, 50, 50);
            button.StateCommon.Content.ShortText.Font = new Font("Segoe UI", 9f);
        }

        private static TableLayoutPanel CreateFieldPanel()
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                AutoSize = true,
                Padding = new Padding(8, 4, 8, 6)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            return panel;
        }

        private static void AddRadioRow(
            TableLayoutPanel panel,
            int row,
            KryptonRadioButton first,
            KryptonRadioButton second)
        {
            var radios = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                Margin = new Padding(0, 2, 0, 4),
                Padding = new Padding(0)
            };
            radios.Controls.Add(first);
            radios.Controls.Add(second);
            panel.Controls.Add(radios, 0, row);
            panel.SetColumnSpan(radios, 3);
        }

        private static void AddField(
            TableLayoutPanel panel,
            int row,
            KryptonLabel label,
            Control input,
            KryptonButton browseButton)
        {
            label.AutoSize = true;
            label.Margin = new Padding(3, 6, 3, 2);
            panel.Controls.Add(label, 0, row);

            input.Dock = DockStyle.Fill;
            input.Margin = new Padding(3, 2, 4, 2);
            panel.Controls.Add(input, 1, row);

            if (browseButton != null)
            {
                browseButton.AutoSize = false;
                browseButton.Size = new Size(88, 28);
                browseButton.Margin = new Padding(0, 1, 2, 1);
                panel.Controls.Add(browseButton, 2, row);
            }
        }
    }
}
