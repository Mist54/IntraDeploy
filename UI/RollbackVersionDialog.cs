using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Krypton.Toolkit;
using IntraDeploy.Models;
using IntraDeploy.Services;
using IntraDeploy.Utilities;
using Serilog;

namespace IntraDeploy.UI
{
    /// <summary>
    /// Lets the operator pick a prior version folder under DeploymentRoot\App\ and
    /// retarget IIS to it (recycle). Does not delete the current deployment folder.
    /// </summary>
    public class RollbackVersionDialog : KryptonForm
    {
        private readonly DeploymentRequest _request;
        private readonly IisService _iisService;
        private readonly string _livePhysicalPath;

        private KryptonListBox _versionList;
        private KryptonLabel _livePathLabel;
        private KryptonButton _rollbackButton;

        /// <summary>True when IIS was successfully retargeted.</summary>
        public bool RollbackSucceeded { get; private set; }

        /// <summary>Version folder that was rolled back to, when successful.</summary>
        public string RolledBackVersion { get; private set; }

        /// <summary>URL after rollback, when available.</summary>
        public string RolledBackUrl { get; private set; }

        public RollbackVersionDialog(DeploymentRequest request, IisService iisService, string livePhysicalPath)
        {
            _request = request;
            _iisService = iisService;
            _livePhysicalPath = livePhysicalPath;
            BuildUi();
            LoadVersions();
        }

        private void BuildUi()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(560, 400);
            Text = "Rollback to previous version";
            Icon = Branding.AppIcon;
            ShowIcon = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 12, 16, 10),
                ColumnCount = 1,
                RowCount = 5
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new KryptonLabel
            {
                Text = "Select a prior version folder. IIS will point at it and recycle. " +
                       "The current folder is not deleted.",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8)
            }, 0, 0);

            _livePathLabel = new KryptonLabel
            {
                Text = string.IsNullOrEmpty(_livePhysicalPath)
                    ? "Current IIS path: (unknown — site/app may not exist yet)"
                    : "Current IIS path: " + _livePhysicalPath,
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8)
            };
            root.Controls.Add(_livePathLabel, 0, 1);

            _versionList = new KryptonListBox
            {
                Dock = DockStyle.Fill
            };
            _versionList.SelectedIndexChanged += (s, e) => UpdateRollbackEnabled();
            root.Controls.Add(_versionList, 0, 2);

            root.Controls.Add(new KryptonLabel
            {
                Text = "Application: " + (_request.ApplicationName ?? "") +
                       "   Root: " + (_request.DeploymentRoot ?? ""),
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 4)
            }, 0, 3);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };

            var cancel = new KryptonButton
            {
                Text = "Cancel",
                AutoSize = false,
                Size = new Size(100, 34),
                DialogResult = DialogResult.Cancel
            };
            _rollbackButton = new KryptonButton
            {
                Text = "Rollback",
                AutoSize = false,
                Size = new Size(110, 34),
                Enabled = false
            };
            _rollbackButton.Click += RollbackButton_Click;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_rollbackButton);
            root.Controls.Add(buttons, 0, 4);

            Controls.Add(root);
            CancelButton = cancel;
            AcceptButton = _rollbackButton;
        }

        private void LoadVersions()
        {
            _versionList.Items.Clear();
            IList<VersionFolderInfo> versions = FileService.ListVersionFolders(
                _request.DeploymentRoot, _request.ApplicationName);

            string liveNorm = Normalize(_livePhysicalPath);
            int added = 0;
            foreach (VersionFolderInfo v in versions)
            {
                bool isLive = !string.IsNullOrEmpty(liveNorm)
                              && string.Equals(Normalize(v.FullPath), liveNorm, StringComparison.OrdinalIgnoreCase);
                string label = v.Version + "  —  " + v.FullPath
                               + (isLive ? "  (current)" : string.Empty);
                _versionList.Items.Add(new VersionListItem
                {
                    Info = v,
                    IsLive = isLive,
                    Display = label
                });
                added++;
            }

            if (added == 0)
            {
                _versionList.Items.Add("(No version folders found under "
                    + Path.Combine(_request.DeploymentRoot ?? "", _request.ApplicationName ?? "") + ")");
            }

            UpdateRollbackEnabled();
        }

        private void UpdateRollbackEnabled()
        {
            VersionListItem item = SelectedItem();
            _rollbackButton.Enabled = item != null && !item.IsLive && item.Info != null
                                      && Directory.Exists(item.Info.FullPath);
        }

        private VersionListItem SelectedItem()
        {
            return _versionList.SelectedItem as VersionListItem;
        }

        private void RollbackButton_Click(object sender, EventArgs e)
        {
            VersionListItem item = SelectedItem();
            if (item == null || item.IsLive || item.Info == null)
            {
                return;
            }

            DialogResult confirm = KryptonMessageBox.Show(this,
                "Point IIS at this version and recycle?\n\n"
                + item.Info.FullPath + "\n\n"
                + "The current deployment folder will remain on disk (not deleted).",
                "Confirm rollback",
                KryptonMessageBoxButtons.YesNo,
                KryptonMessageBoxIcon.Warning,
                KryptonMessageBoxDefaultButton.Button2);

            if (confirm != DialogResult.Yes)
            {
                return;
            }

            _rollbackButton.Enabled = false;
            try
            {
                _iisService.RetargetPhysicalPathAndRecycle(_request, item.Info.FullPath);
                string url = null;
                try
                {
                    url = _iisService.BuildUrl(_request);
                }
                catch (Exception)
                {
                    url = null;
                }

                RollbackSucceeded = true;
                RolledBackVersion = item.Info.Version;
                RolledBackUrl = url;

                // Update request version so history / form stay consistent with the live folder.
                _request.Version = item.Info.Version;

                DeployHistoryStore.Append(DeployHistoryStore.FromRollback(
                    _request, item.Info.Version, item.Info.FullPath, url, true, null));

                Log.Information("Operator rollback succeeded: {App} → {Version} at {Path}",
                    _request.ApplicationName, item.Info.Version, item.Info.FullPath);

                KryptonMessageBox.Show(this,
                    "IIS now points at:\n" + item.Info.FullPath
                    + (string.IsNullOrEmpty(url) ? string.Empty : "\n\nURL: " + url),
                    "Rollback complete",
                    KryptonMessageBoxButtons.OK,
                    KryptonMessageBoxIcon.Information);

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Operator rollback failed for {App}", _request.ApplicationName);
                DeployHistoryStore.Append(DeployHistoryStore.FromRollback(
                    _request, item.Info.Version, item.Info.FullPath, null, false, ex.Message));

                KryptonMessageBox.Show(this,
                    "Rollback failed:\n\n" + ex.Message,
                    "Rollback failed",
                    KryptonMessageBoxButtons.OK,
                    KryptonMessageBoxIcon.Error);
                _rollbackButton.Enabled = true;
            }
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
            }
            catch (Exception)
            {
                return path.TrimEnd('\\', '/').ToLowerInvariant();
            }
        }

        private sealed class VersionListItem
        {
            public VersionFolderInfo Info { get; set; }
            public bool IsLive { get; set; }
            public string Display { get; set; }

            public override string ToString()
            {
                return Display ?? string.Empty;
            }
        }
    }
}
