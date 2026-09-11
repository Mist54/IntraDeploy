using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Krypton.Toolkit;
using IntraDeploy.Models;

namespace IntraDeploy.UI
{
    /// <summary>
    /// Post-deployment result dialog. Shows exactly what was deployed (application,
    /// version, deployment folder, IIS target, URL, database action, health) using the
    /// real values from the DeploymentRequest/DeploymentResult, plus copy/open actions.
    /// Nothing in here invents data: rows are only shown when the underlying value exists.
    /// </summary>
    public class DeploymentResultDialog : KryptonForm
    {
        private const int LabelColumnWidth = 150;

        private readonly DeploymentRequest _request;
        private readonly DeploymentResult _result;

        private KryptonButton _copyPathButton;
        private KryptonButton _openFolderButton;
        private KryptonButton _copyUrlButton;
        private KryptonButton _openUrlButton;
        private KryptonButton _rollbackButton;
        private Timer _buttonFeedbackTimer;

        /// <summary>
        /// True when the operator clicked Rollback — MainForm should open the version picker.
        /// </summary>
        public bool RollbackRequested { get; private set; }

        public DeploymentResultDialog(DeploymentRequest request, DeploymentResult result)
        {
            _request = request;
            _result = result;
            BuildUi();
        }

        // ------------------------------------------------------------------
        // Construction
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(560, string.IsNullOrEmpty(_result.DryRunSummary) ? 440 : 520);
            Text = DescribeWindowTitle();
            Icon = Branding.AppIcon;
            ShowIcon = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18, 14, 18, 8),
                ColumnCount = 1,
                RowCount = 4
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(BuildHeader(), 0, 0);
            root.Controls.Add(BuildDetailGrid(), 0, 1);
            root.Controls.Add(BuildActionButtons(), 0, 2);
            root.Controls.Add(BuildCloseRow(), 0, 3);

            Controls.Add(root);
        }

        private string DescribeWindowTitle()
        {
            if (!string.IsNullOrEmpty(_result.DryRunSummary) && _result.Success)
            {
                return "Dry-run completed";
            }
            return _result.Success ? "Deployment successful" : DescribeFailureTitle();
        }

        private KryptonLabel BuildHeader()
        {
            string text;
            Color color;
            if (!string.IsNullOrEmpty(_result.DryRunSummary) && _result.Success)
            {
                text = "✓  Dry-run Completed (no changes)";
                color = Color.FromArgb(0, 99, 177);
            }
            else if (_result.Success)
            {
                text = "✓  Deployment Successful";
                color = Color.FromArgb(0, 122, 63); // enterprise green
            }
            else if (_result.FailedAt == DeploymentStep.NotStarted)
            {
                text = "—  Deployment Cancelled";
                color = Color.FromArgb(120, 120, 120);
            }
            else if (_result.FailedAt == DeploymentStep.HealthCheck)
            {
                text = "✕  Deployment Failed — Health Check";
                color = Color.FromArgb(176, 0, 32);
            }
            else if (_result.FailedAt == DeploymentStep.PostDeployHook)
            {
                text = "✕  Deployment Failed — Post-deploy Hook";
                color = Color.FromArgb(176, 0, 32);
            }
            else
            {
                text = "✕  Deployment Failed";
                color = Color.FromArgb(176, 0, 32); // enterprise red
            }

            return new KryptonLabel
            {
                Text = text,
                AutoSize = true,
                StateCommon =
                {
                    ShortText =
                    {
                        Color1 = color,
                        Font = new Font("Segoe UI", 13f, FontStyle.Bold)
                    }
                },
                Margin = new Padding(3, 0, 3, 10)
            };
        }

        private TableLayoutPanel BuildDetailGrid()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LabelColumnWidth));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            if (_result.Success)
            {
                AddRow(grid, "Application:", _request.ApplicationName);
                AddRow(grid, "Version:", _request.Version);
                AddRow(grid, "Mode:", DeploymentPlan.DescribeMode(_request.Mode));
                if (!string.IsNullOrEmpty(_result.DryRunSummary))
                {
                    AddWrappedRow(grid, "Dry-run plan:", _result.DryRunSummary, subdued: false);
                }
                else
                {
                    AddRow(grid, "Deployment Folder:", _result.TargetFolder);
                    AddRow(grid, "IIS:", DescribeIis());
                    if (HasValidUrl)
                    {
                        AddRow(grid, "URL:", _result.HealthCheckUrl);
                    }
                    AddRow(grid, "Database:", DescribeDatabase());
                    AddRow(grid, "Health:", DescribeHealth());
                    if (!string.IsNullOrEmpty(_result.HealthCheckSummary))
                    {
                        AddWrappedRow(grid, string.Empty, _result.HealthCheckSummary, subdued: true);
                    }
                    if (_result.PostDeployHookExitCode != null)
                    {
                        AddRow(grid, "Post-deploy:",
                            _result.PostDeployHookExitCode.Value == 0
                                ? "Hooks OK (exit 0)"
                                : "Hooks warning (exit " + _result.PostDeployHookExitCode.Value + ")");
                        if (!string.IsNullOrEmpty(_result.PostDeployHookSummary))
                        {
                            AddWrappedRow(grid, string.Empty, _result.PostDeployHookSummary, subdued: true);
                        }
                    }
                }
            }
            else
            {
                if (_result.FailedAt != DeploymentStep.NotStarted)
                {
                    AddRow(grid, "Failed Step:", DescribeStep(_result.FailedAt));
                }
                AddWrappedRow(grid, "Reason:", _result.ErrorMessage ?? "(no error message)", subdued: false);
                if (!string.IsNullOrEmpty(_result.RollbackNote))
                {
                    AddWrappedRow(grid, "Rollback:", _result.RollbackNote, subdued: false);
                }
                if (!string.IsNullOrEmpty(_result.TargetFolder))
                {
                    AddRow(grid, "Deployment Folder:", _result.TargetFolder);
                }
                if (HasValidUrl)
                {
                    AddRow(grid, "URL:", _result.HealthCheckUrl);
                }
                if (_result.Health != HealthCheckOutcome.NotRun)
                {
                    AddRow(grid, "Health:", DescribeHealth());
                    if (!string.IsNullOrEmpty(_result.HealthCheckSummary))
                    {
                        AddWrappedRow(grid, string.Empty, _result.HealthCheckSummary, subdued: true);
                    }
                }
                if (_result.PostDeployHookExitCode != null && !string.IsNullOrEmpty(_result.PostDeployHookSummary))
                {
                    AddWrappedRow(grid, "Post-deploy:", _result.PostDeployHookSummary, subdued: true);
                }
            }

            return grid;
        }

        private FlowLayoutPanel BuildActionButtons()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 4)
            };

            _copyPathButton = CreateActionButton("Copy Path", CopyPath);
            _openFolderButton = CreateActionButton("Open Folder", OpenFolder);
            _copyUrlButton = CreateActionButton("Copy URL", CopyUrl);
            _openUrlButton = CreateActionButton("Open URL", OpenUrl);
            _rollbackButton = CreateActionButton("Rollback...", RequestRollback);
            _rollbackButton.Size = new Size(120, 32);

            _copyPathButton.Enabled = !string.IsNullOrEmpty(_result.TargetFolder);
            _openFolderButton.Enabled = !string.IsNullOrEmpty(_result.TargetFolder)
                                        && Directory.Exists(_result.TargetFolder);
            _copyUrlButton.Enabled = HasValidUrl;
            _openUrlButton.Enabled = HasValidUrl;
            _rollbackButton.Enabled = _request != null
                                      && !string.IsNullOrWhiteSpace(_request.ApplicationName)
                                      && !string.IsNullOrWhiteSpace(_request.DeploymentRoot);

            // Keep the tab order tidy even when disabled buttons are skipped.
            panel.Controls.Add(_copyPathButton);
            panel.Controls.Add(_openFolderButton);
            panel.Controls.Add(_copyUrlButton);
            panel.Controls.Add(_openUrlButton);
            panel.Controls.Add(_rollbackButton);
            return panel;
        }

        private FlowLayoutPanel BuildCloseRow()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 4, 0, 4)
            };
            var close = new KryptonButton
            {
                Text = "Close",
                AutoSize = false,
                Size = new Size(100, 34),
                Margin = new Padding(0),
                DialogResult = DialogResult.Cancel
            };
            panel.Controls.Add(close);
            AcceptButton = close;
            CancelButton = close;
            return panel;
        }

        private static KryptonButton CreateActionButton(string text, EventHandler onClick)
        {
            var button = new KryptonButton
            {
                Text = text,
                AutoSize = false,
                Size = new Size(110, 32),
                Margin = new Padding(0, 0, 10, 0)
            };
            button.Click += onClick;
            return button;
        }

        // ------------------------------------------------------------------
        // Values (all real, from the request/result)
        // ------------------------------------------------------------------

        private bool HasValidUrl
        {
            get
            {
                Uri uri;
                return !string.IsNullOrEmpty(_result.HealthCheckUrl)
                       && Uri.TryCreate(_result.HealthCheckUrl, UriKind.Absolute, out uri)
                       && (uri.Scheme == "http" || uri.Scheme == "https");
            }
        }

        private string DescribeIis()
        {
            if (_request.Iis.Mode == IisMode.ApplicationUnderSite)
            {
                return _request.Iis.ParentSiteName + " → " + _request.Iis.NormalizedApplicationPath;
            }
            return _request.Iis.SiteName + " (port " + _request.Iis.Port + ")";
        }

        private string DescribeDatabase()
        {
            if (!DeploymentPlan.RunsDatabase(_request.Mode))
            {
                return "Skipped (mode)";
            }
            if (_request.Database.Mode == DatabaseMode.UseExisting)
            {
                return "Existing database — no database changes made";
            }
            string file = Path.GetFileName(_request.Database.BackupFilePath);
            return "Restored from " + file
                   + (_request.Database.ConfirmDatabaseOverwrite ? " (existing database overwritten)" : string.Empty);
        }

        private string DescribeHealth()
        {
            switch (_result.Health)
            {
                case HealthCheckOutcome.Healthy:
                    return "Healthy";
                case HealthCheckOutcome.UnhealthyResponse:
                    return "FAILED — application answered with an error status";
                case HealthCheckOutcome.Unreachable:
                    return "FAILED — application unreachable";
                default:
                    return "Not run";
            }
        }

        /// <summary>Operator friendly pipeline step name.</summary>
        public static string DescribeStep(DeploymentStep step)
        {
            switch (step)
            {
                case DeploymentStep.ValidateInputs: return "Input validation";
                case DeploymentStep.ProbeApplication: return "Analyze published application";
                case DeploymentStep.CheckIisAvailability: return "IIS availability check";
                case DeploymentStep.CheckSqlServer: return "SQL Server validation";
                case DeploymentStep.PrepareTargetFolder: return "Prepare deployment folder";
                case DeploymentStep.CopyApplicationFiles: return "Copy deployment files";
                case DeploymentStep.ConfigureIis: return "IIS configuration";
                case DeploymentStep.DatabaseOperation: return "Database restore";
                case DeploymentStep.ApplyConfiguration: return "Apply configuration";
                case DeploymentStep.StartAndRecycle: return "Start / recycle IIS";
                case DeploymentStep.HealthCheck: return "Health check";
                case DeploymentStep.PostDeployHook: return "Post-deploy hook";
                case DeploymentStep.Completed: return "Completed";
                default: return "Deployment";
            }
        }

        private string DescribeFailureTitle()
        {
            return _result.FailedAt == DeploymentStep.NotStarted
                ? "Deployment cancelled"
                : "Deployment failed — " + DescribeStep(_result.FailedAt);
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------

        private void CopyPath(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_result.TargetFolder))
            {
                TrySetClipboard(_result.TargetFolder, _copyPathButton);
            }
        }

        private void CopyUrl(object sender, EventArgs e)
        {
            if (HasValidUrl)
            {
                TrySetClipboard(_result.HealthCheckUrl, _copyUrlButton);
            }
        }

        private void OpenFolder(object sender, EventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(_result.TargetFolder) && Directory.Exists(_result.TargetFolder))
                {
                    using (Process.Start(new ProcessStartInfo
                    {
                        FileName = _result.TargetFolder,
                        UseShellExecute = true // open folder in Explorer (not a shell command)
                    })) { }
                }
            }
            catch (Exception ex)
            {
                KryptonMessageBox.Show(this, "Could not open the deployment folder: " + ex.Message,
                    "Open folder", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
            }
        }

        private void OpenUrl(object sender, EventArgs e)
        {
            try
            {
                if (HasValidUrl)
                {
                    using (Process.Start(new ProcessStartInfo
                    {
                        FileName = _result.HealthCheckUrl,
                        UseShellExecute = true // open URL in the default browser
                    })) { }
                }
            }
            catch (Exception ex)
            {
                KryptonMessageBox.Show(this, "Could not open the URL: " + ex.Message,
                    "Open URL", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
            }
        }

        private void RequestRollback(object sender, EventArgs e)
        {
            RollbackRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void TrySetClipboard(string value, KryptonButton sourceButton)
        {
            try
            {
                Clipboard.SetText(value);
                FlashCopied(sourceButton);
            }
            catch (Exception ex)
            {
                KryptonMessageBox.Show(this, "Could not copy to the clipboard: " + ex.Message,
                    "Copy", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
            }
        }

        /// <summary>Small confirmation: the button text flips to "Copied!" for a moment.</summary>
        private void FlashCopied(KryptonButton button)
        {
            if (button == null)
            {
                return;
            }
            string original = button.Text;
            button.Text = "Copied!";
            button.Enabled = false;

            if (_buttonFeedbackTimer == null)
            {
                _buttonFeedbackTimer = new Timer { Interval = 1200 };
                _buttonFeedbackTimer.Tick += (s, e) =>
                {
                    _buttonFeedbackTimer.Stop();
                    RestoreButtons();
                };
            }
            _buttonFeedbackTimer.Tag = original;
            _buttonFeedbackTimer.Start();
        }

        private void RestoreButtons()
        {
            if (_buttonFeedbackTimer == null || _buttonFeedbackTimer.Tag == null)
            {
                return;
            }
            string original = (string)_buttonFeedbackTimer.Tag;
            _buttonFeedbackTimer.Tag = null;
            if (_copyPathButton != null)
            {
                _copyPathButton.Text = _copyPathButton.Text == "Copied!" ? original : _copyPathButton.Text;
                _copyPathButton.Enabled = !string.IsNullOrEmpty(_result.TargetFolder);
            }
            if (_copyUrlButton != null)
            {
                _copyUrlButton.Text = _copyUrlButton.Text == "Copied!" ? original : _copyUrlButton.Text;
                _copyUrlButton.Enabled = HasValidUrl;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _buttonFeedbackTimer != null)
            {
                _buttonFeedbackTimer.Dispose();
                _buttonFeedbackTimer = null;
            }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------
        // Grid helpers
        // ------------------------------------------------------------------

        private static void AddRow(TableLayoutPanel grid, string labelText, string valueText)
        {
            grid.Controls.Add(new KryptonLabel
            {
                Text = labelText,
                LabelStyle = LabelStyle.BoldControl,
                AutoSize = true,
                Margin = new Padding(3, 5, 3, 3)
            }, 0, grid.RowCount);

            grid.Controls.Add(new KryptonLabel
            {
                Text = valueText,
                AutoSize = true,
                Margin = new Padding(3, 5, 3, 3)
            }, 1, grid.RowCount);
            grid.RowCount++;
        }

        /// <summary>
        /// Adds a value that can span several lines (error messages, health summaries).
        /// The label height is measured against the available width so the row stays tight.
        /// </summary>
        private void AddWrappedRow(TableLayoutPanel grid, string labelText, string valueText, bool subdued)
        {
            if (labelText.Length > 0)
            {
                grid.Controls.Add(new KryptonLabel
                {
                    Text = labelText,
                    LabelStyle = LabelStyle.BoldControl,
                    AutoSize = true,
                    Margin = new Padding(3, 5, 3, 3)
                }, 0, grid.RowCount);
            }
            else
            {
                grid.Controls.Add(new Panel { Margin = new Padding(0) }, 0, grid.RowCount);
            }

            int availableWidth = ClientSize.Width - LabelColumnWidth - 40;
            var wrap = new KryptonWrapLabel
            {
                Text = valueText,
                Dock = DockStyle.Fill,
                AutoSize = false,
                Margin = new Padding(3, 5, 3, 3)
            };
            if (subdued)
            {
                wrap.StateCommon.TextColor = Color.FromArgb(96, 96, 96);
                wrap.Font = new Font("Segoe UI", 8.5f);
            }

            Size measured = TextRenderer.MeasureText(
                valueText, wrap.Font, new Size(Math.Max(availableWidth, 200), int.MaxValue),
                TextFormatFlags.WordBreak);
            wrap.Height = measured.Height + 4;

            grid.Controls.Add(wrap, 1, grid.RowCount);
            grid.RowCount++;
        }
    }
}
