using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;
using IntraDeploy.Models;

namespace IntraDeploy.UI
{
    /// <summary>
    /// Shows the deployment summary before anything is executed.
    /// Returns DialogResult.Yes for "Deploy", No/Cancel for abort.
    ///
    /// The dialog shows LIVE IIS state (what IIS actually contains and what IntraDeploy
    /// will do), not merely what the operator typed. The database overwrite checkbox is
    /// the single source of truth for DatabaseSettings.ConfirmDatabaseOverwrite and is
    /// written back into the request before the dialog closes.
    /// </summary>
    public class DeploymentConfirmationDialog : KryptonForm
    {
        private readonly DeploymentRequest _request;
        private readonly Func<string, bool> _databaseExistsCheck;
        private readonly IisLiveState _liveState;

        private KryptonCheckBox _overwriteDatabaseCheckBox;
        private KryptonButton _deployButton;
        private TableLayoutPanel _layout;
        private int _nextRow;

        public DeploymentConfirmationDialog(
            DeploymentRequest request,
            Func<string, bool> databaseExistsCheck,
            IisLiveState liveState)
        {
            _request = request;
            _databaseExistsCheck = databaseExistsCheck;
            _liveState = liveState;
            BuildUi();
        }

        /// <summary>True when the operator ticked the explicit database-overwrite box.</summary>
        public bool DatabaseOverwriteConfirmed
        {
            get { return _overwriteDatabaseCheckBox != null && _overwriteDatabaseCheckBox.Checked; }
        }

        private void BuildUi()
        {
            Text = _request.DryRun ? "Confirm dry-run" : "Confirm deployment";
            Icon = Branding.AppIcon;
            ShowIcon = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(660, 720);
            Font = new Font("Segoe UI", 9.5f);

            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 14, 16, 4),
                ColumnCount = 2,
                AutoScroll = true
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            AddRow("Application:", _request.ApplicationName);
            AddRow("Version:", _request.Version);
            AddRow("Source:", _request.SourceFolder);
            AddRow("Deploy mode:", DeploymentPlan.DescribeMode(_request.Mode));
            AddRow("Dry-run:", _request.DryRun ? "Yes — no writes" : "No");

            AddSection("Steps that will run");
            foreach (string step in DeploymentPlan.ListOperatorSteps(_request))
            {
                if (step.StartsWith("Mode:", StringComparison.Ordinal))
                {
                    continue;
                }
                AddRow("•", step);
            }

            // --- IIS section: live state, not typed values -------------------------
            AddSection(_request.Iis.Mode == IisMode.ApplicationUnderSite
                ? "IIS (Application under site)"
                : "IIS (Separate site)");

            if (_request.Iis.Mode == IisMode.ApplicationUnderSite)
            {
                AddRow("Parent Site:", _request.Iis.ParentSiteName);
                AddRow("Application:", _request.Iis.NormalizedApplicationPath);
            }
            else
            {
                AddRow("IIS Site:", _request.Iis.SiteName);
                AddRow("HTTP Port:", _request.Iis.Port.ToString());
                if (_request.Iis.HasHttpsBinding)
                {
                    string tip = IntraDeploy.Services.IisService.NormalizeThumbprint(
                        _request.Iis.HttpsCertificateThumbprint);
                    if (tip.Length > 8)
                    {
                        tip = tip.Substring(tip.Length - 8);
                    }
                    AddRow("HTTPS Port:", _request.Iis.HttpsPort.ToString() +
                                         (string.IsNullOrEmpty(tip) ? string.Empty : " (cert …" + tip + ")"));
                }
                else
                {
                    AddRow("HTTPS:", "Off (HTTP only)");
                }
            }
            AddRow("Application Pool:", _request.Iis.ApplicationPoolName);
            AddRow("New Physical Path:", _request.TargetFolder);

            AddRow("Action:", _liveState != null ? _liveState.Action : "(IIS state unavailable)");
            if (_liveState != null)
            {
                if (!string.IsNullOrEmpty(_liveState.CurrentPhysicalPath))
                {
                    AddRow("Current Physical Path:", _liveState.CurrentPhysicalPath);
                }
                if (!string.IsNullOrEmpty(_liveState.CurrentApplicationPool))
                {
                    AddRow("Current Pool:", _liveState.CurrentApplicationPool);
                }
                if (!string.IsNullOrEmpty(_liveState.ParentSiteBindings))
                {
                    AddRow("Parent Site Bindings:", _liveState.ParentSiteBindings);
                }
                if (!string.IsNullOrEmpty(_liveState.Details))
                {
                    AddRow("Details:", _liveState.Details);
                }
            }

            // --- Database section ---------------------------------------------------
            AddSection("Database");
            AddRow("Database:", _request.Database.DatabaseName);
            AddRow("SQL Auth:", DescribeSqlAuth());
            AddRow("Database Action:", DescribeDatabaseAction());

            // --- Configuration (connection string) ----------------------------------
            AddSection("Configuration");
            AddRow("Connection String:", DescribeConnectionStringAction());
            AddRow("Health Check Path:", DescribeHealthCheckPath());
            AddRow("Post-deploy hooks:", DescribePostDeployHooks());

            var warningLabel = new KryptonLabel
            {
                Text = DescribeWarnings(),
                LabelStyle = LabelStyle.AlternateControl,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 64,
                Margin = new Padding(3, 10, 3, 3)
            };
            _layout.Controls.Add(warningLabel, 0, _nextRow++);
            _layout.SetColumnSpan(warningLabel, 2);

            // Single source of truth for the overwrite decision.
            _overwriteDatabaseCheckBox = new KryptonCheckBox
            {
                Text = "I understand this will OVERWRITE the existing database",
                Checked = false,
                Visible = RequiresOverwriteConfirmation,
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 3)
            };
            _overwriteDatabaseCheckBox.CheckedChanged += (s, e) => UpdateButtonState();
            _layout.Controls.Add(_overwriteDatabaseCheckBox, 0, _nextRow++);
            _layout.SetColumnSpan(_overwriteDatabaseCheckBox, 2);

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(16, 10, 16, 12),
                Height = 58
            };

            _deployButton = new KryptonButton
            {
                Text = _request.DryRun ? "DRY-RUN" : "DEPLOY",
                AutoSize = false,
                Size = new Size(120, 34),
                Margin = new Padding(12, 0, 0, 0),
                Enabled = !RequiresOverwriteConfirmation
            };
            _deployButton.StateCommon.Content.ShortText.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            _deployButton.Click += (s, e) =>
            {
                // Write the confirmation back into the request: the one source of truth.
                _request.Database.ConfirmDatabaseOverwrite = DatabaseOverwriteConfirmed;
                DialogResult = DialogResult.Yes;
                Close();
            };

            var cancelButton = new KryptonButton
            {
                Text = "Cancel",
                AutoSize = false,
                Size = new Size(100, 34),
                Margin = new Padding(0)
            };
            cancelButton.Click += (s, e) => { DialogResult = DialogResult.No; Close(); };

            // RTL: first control is rightmost (DEPLOY), then Cancel to its left with gap.
            buttonPanel.Controls.Add(_deployButton);
            buttonPanel.Controls.Add(cancelButton);

            Controls.Add(_layout);
            Controls.Add(buttonPanel);
            AcceptButton = _deployButton;
            CancelButton = cancelButton;
        }

        private void UpdateButtonState()
        {
            if (RequiresOverwriteConfirmation)
            {
                _deployButton.Enabled = _overwriteDatabaseCheckBox.Checked;
            }
        }

        private bool RequiresOverwriteConfirmation
        {
            get
            {
                if (_request.DryRun)
                {
                    return false;
                }
                return DeploymentPlan.WillRestoreDatabase(_request)
                    && _databaseExistsCheck != null
                    && _databaseExistsCheck(_request.Database.DatabaseName);
            }
        }

        private string DescribeDatabaseAction()
        {
            if (!DeploymentPlan.RunsDatabase(_request.Mode))
            {
                return "SKIPPED (mode does not include database)";
            }
            if (_request.Database.Mode == DatabaseMode.UseExisting)
            {
                return "NONE (use existing database; IntraDeploy will not touch the database)";
            }
            return "RESTORE from " + _request.Database.BackupFilePath;
        }

        private string DescribeSqlAuth()
        {
            if (_request.Database == null || string.IsNullOrWhiteSpace(_request.Database.SqlUser))
            {
                return "Windows authentication";
            }
            // Never show the password.
            return "SQL login (" + _request.Database.SqlUser + ")";
        }

        private string DescribeConnectionStringAction()
        {
            if (!DeploymentPlan.RunsApplyConfiguration(_request.Mode))
            {
                return "SKIPPED (mode does not copy files / apply config)";
            }
            if (!_request.UpdateConnectionString)
            {
                return "Leave deployed config unchanged";
            }
            if (string.IsNullOrWhiteSpace(_request.ConnectionString))
            {
                return "UPDATE enabled but connection string is empty (will fail at Apply configuration)";
            }
            // Never display the connection string value (may contain a password).
            return "Update first connection string in the deployed copy only (source untouched)";
        }

        private string DescribeHealthCheckPath()
        {
            string path = string.IsNullOrWhiteSpace(_request.HealthCheckPath)
                ? IntraDeploy.Configuration.AppConfig.HealthCheckPath
                : _request.HealthCheckPath.Trim();
            return path + " (timeout " + IntraDeploy.Configuration.AppConfig.HealthCheckTimeoutSeconds +
                   "s; fail-deploy=" + IntraDeploy.Configuration.AppConfig.HealthCheckFailureFailsDeploy + ")";
        }

        private string DescribePostDeployHooks()
        {
            string raw = _request.PostDeployHooks != null
                ? _request.PostDeployHooks
                : IntraDeploy.Configuration.AppConfig.PostDeployHooks;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return "None";
            }
            string[] parts = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            int count = 0;
            foreach (string p in parts)
            {
                if (p.Trim().Length > 0)
                {
                    count++;
                }
            }
            return count + " configured (timeout "
                + IntraDeploy.Configuration.AppConfig.PostDeployHookTimeoutSeconds
                + "s; fail-deploy="
                + IntraDeploy.Configuration.AppConfig.PostDeployHookFailureFailsDeploy + ")";
        }

        private string DescribeWarnings()
        {
            if (_request.DryRun)
            {
                return "DRY-RUN: IntraDeploy will not copy files, change IIS, restore SQL, or write config.";
            }
            string warnings = string.Empty;
            if (DeploymentPlan.WillRestoreDatabase(_request))
            {
                warnings += "DESTRUCTIVE: a restore will replace the contents of database '" +
                            _request.Database.DatabaseName + "'. ";
            }
            if (DeploymentPlan.RunsCopy(_request.Mode)
                && System.IO.Directory.Exists(_request.TargetFolder)
                && _request.AllowOverwriteExistingFolder)
            {
                warnings += "The existing deployment folder will be overwritten (backup-swap: the previous version " +
                            "is restored automatically if the copy fails). ";
            }
            if (string.IsNullOrEmpty(warnings))
            {
                warnings = "No destructive operations planned.";
            }
            return warnings.Trim();
        }

        private void AddSection(string title)
        {
            var label = new KryptonLabel
            {
                Text = title,
                LabelStyle = LabelStyle.TitleControl,
                AutoSize = true,
                Margin = new Padding(3, 14, 3, 3)
            };
            _layout.Controls.Add(label, 0, _nextRow++);
            _layout.SetColumnSpan(label, 2);
        }

        private void AddRow(string labelText, string valueText)
        {
            var labelControl = new KryptonLabel
            {
                Text = labelText,
                LabelStyle = LabelStyle.BoldControl,
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 3)
            };
            var valueControl = new KryptonLabel
            {
                Text = valueText,
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 3)
            };
            _layout.Controls.Add(labelControl, 0, _nextRow);
            _layout.Controls.Add(valueControl, 1, _nextRow);
            _nextRow++;
        }
    }
}
