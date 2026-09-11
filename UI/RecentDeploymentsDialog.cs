using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Krypton.Toolkit;
using IntraDeploy.Models;
using IntraDeploy.Utilities;

namespace IntraDeploy.UI
{
    /// <summary>
    /// Shows recent deploy/rollback history from history.jsonl.
    /// Operator can fill the main form, open the folder, or open the URL.
    /// </summary>
    public class RecentDeploymentsDialog : KryptonForm
    {
        private KryptonListBox _list;
        private KryptonButton _fillFormButton;
        private KryptonButton _openFolderButton;
        private KryptonButton _openUrlButton;

        /// <summary>Entry chosen for "Fill form", or null if cancelled / other action.</summary>
        public DeployHistoryEntry SelectedForFill { get; private set; }

        public RecentDeploymentsDialog()
        {
            BuildUi();
            LoadEntries();
        }

        private void BuildUi()
        {
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(640, 420);
            Text = "Recent deployments";
            Icon = Branding.AppIcon;
            ShowIcon = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16, 12, 16, 10),
                ColumnCount = 1,
                RowCount = 3
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.Controls.Add(new KryptonLabel
            {
                Text = "Recent runs (no secrets). Double-click or Fill form to load app/version into the main window.",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8)
            }, 0, 0);

            _list = new KryptonListBox
            {
                Dock = DockStyle.Fill
            };
            _list.SelectedIndexChanged += (s, e) => UpdateButtons();
            _list.DoubleClick += (s, e) => FillForm();
            root.Controls.Add(_list, 0, 1);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0)
            };

            _fillFormButton = CreateButton("Fill form", (s, e) => FillForm());
            _openFolderButton = CreateButton("Open folder", (s, e) => OpenFolder());
            _openUrlButton = CreateButton("Open URL", (s, e) => OpenUrl());
            var close = CreateButton("Close", null);
            close.DialogResult = DialogResult.Cancel;

            buttons.Controls.Add(_fillFormButton);
            buttons.Controls.Add(_openFolderButton);
            buttons.Controls.Add(_openUrlButton);

            var right = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Dock = DockStyle.Fill
            };
            // Spacer row: put Close on the right via a nested layout
            var buttonRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                AutoSize = true,
                Margin = new Padding(0, 8, 0, 0)
            };
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            buttonRow.Controls.Add(buttons, 0, 0);
            right.Controls.Add(close);
            buttonRow.Controls.Add(right, 1, 0);

            root.Controls.Add(buttonRow, 0, 2);
            Controls.Add(root);
            CancelButton = close;
            AcceptButton = _fillFormButton;
            UpdateButtons();
        }

        private static KryptonButton CreateButton(string text, EventHandler onClick)
        {
            var button = new KryptonButton
            {
                Text = text,
                AutoSize = false,
                Size = new Size(110, 32),
                Margin = new Padding(0, 0, 8, 0)
            };
            if (onClick != null)
            {
                button.Click += onClick;
            }
            return button;
        }

        private void LoadEntries()
        {
            _list.Items.Clear();
            IList<DeployHistoryEntry> entries = DeployHistoryStore.ReadRecent(50);
            if (entries.Count == 0)
            {
                _list.Items.Add("(No history yet — " + DeployHistoryStore.HistoryFilePath + ")");
                return;
            }

            foreach (DeployHistoryEntry entry in entries)
            {
                _list.Items.Add(new HistoryListItem { Entry = entry, Display = FormatLine(entry) });
            }
        }

        private static string FormatLine(DeployHistoryEntry e)
        {
            string when = e.TimestampUtc == default(DateTime)
                ? "?"
                : e.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            string op = string.IsNullOrEmpty(e.Operation) ? "Deploy" : e.Operation;
            string status = e.Success ? "OK" : ("FAIL" + (string.IsNullOrEmpty(e.FailedStep) ? "" : " @" + e.FailedStep));
            return when + "  " + op + "  " + status + "  "
                   + (e.ApplicationName ?? "?") + " v" + (e.Version ?? "?")
                   + (string.IsNullOrEmpty(e.IisTarget) ? "" : "  [" + e.IisTarget + "]");
        }

        private DeployHistoryEntry SelectedEntry()
        {
            HistoryListItem item = _list.SelectedItem as HistoryListItem;
            return item != null ? item.Entry : null;
        }

        private void UpdateButtons()
        {
            DeployHistoryEntry entry = SelectedEntry();
            bool has = entry != null;
            _fillFormButton.Enabled = has && !string.IsNullOrWhiteSpace(entry.ApplicationName);
            _openFolderButton.Enabled = has && !string.IsNullOrWhiteSpace(entry.TargetFolder)
                                        && Directory.Exists(entry.TargetFolder);
            Uri uri;
            _openUrlButton.Enabled = has && !string.IsNullOrWhiteSpace(entry.Url)
                                     && Uri.TryCreate(entry.Url, UriKind.Absolute, out uri)
                                     && (uri.Scheme == "http" || uri.Scheme == "https");
        }

        private void FillForm()
        {
            DeployHistoryEntry entry = SelectedEntry();
            if (entry == null || string.IsNullOrWhiteSpace(entry.ApplicationName))
            {
                return;
            }
            SelectedForFill = entry;
            DialogResult = DialogResult.OK;
            Close();
        }

        private void OpenFolder()
        {
            DeployHistoryEntry entry = SelectedEntry();
            if (entry == null || string.IsNullOrWhiteSpace(entry.TargetFolder) || !Directory.Exists(entry.TargetFolder))
            {
                return;
            }
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = entry.TargetFolder,
                    UseShellExecute = true
                })) { }
            }
            catch (Exception ex)
            {
                KryptonMessageBox.Show(this, "Could not open folder: " + ex.Message,
                    "Open folder", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
            }
        }

        private void OpenUrl()
        {
            DeployHistoryEntry entry = SelectedEntry();
            Uri uri;
            if (entry == null || string.IsNullOrWhiteSpace(entry.Url)
                || !Uri.TryCreate(entry.Url, UriKind.Absolute, out uri)
                || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                return;
            }
            try
            {
                using (Process.Start(new ProcessStartInfo
                {
                    FileName = entry.Url,
                    UseShellExecute = true
                })) { }
            }
            catch (Exception ex)
            {
                KryptonMessageBox.Show(this, "Could not open URL: " + ex.Message,
                    "Open URL", KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
            }
        }

        private sealed class HistoryListItem
        {
            public DeployHistoryEntry Entry { get; set; }
            public string Display { get; set; }

            public override string ToString()
            {
                return Display ?? string.Empty;
            }
        }
    }
}
