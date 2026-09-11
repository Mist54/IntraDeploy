using System;
using System.Drawing;
using System.Windows.Forms;
using Krypton.Toolkit;

namespace IntraDeploy.UI
{
    /// <summary>
    /// Session-only SQL login prompt. Credentials are held in memory on the returned
    /// properties; callers must not write them to disk or logs.
    /// </summary>
    public class SqlCredentialsDialog : KryptonForm
    {
        private readonly KryptonTextBox _userTextBox;
        private readonly KryptonTextBox _passwordTextBox;

        public string SqlUser
        {
            get { return _userTextBox.Text.Trim(); }
        }

        public string SqlPassword
        {
            get { return _passwordTextBox.Text; }
        }

        public SqlCredentialsDialog(string initialUser)
        {
            Text = "SQL Server authentication";
            Icon = Branding.AppIcon;
            ShowIcon = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(420, 180);
            Font = new Font("Segoe UI", 9f);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(16, 14, 16, 8)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var hint = new KryptonLabel
            {
                Text = "Credentials are kept for this IntraDeploy session only and are never saved to disk.",
                AutoSize = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 0, 3, 10)
            };
            layout.Controls.Add(hint, 0, 0);
            layout.SetColumnSpan(hint, 2);

            var userLabel = new KryptonLabel { Text = "SQL User", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _userTextBox = new KryptonTextBox
            {
                Dock = DockStyle.Fill,
                Text = initialUser ?? string.Empty,
                Margin = new Padding(3, 3, 3, 3)
            };
            layout.Controls.Add(userLabel, 0, 1);
            layout.Controls.Add(_userTextBox, 1, 1);

            var passwordLabel = new KryptonLabel { Text = "Password", AutoSize = true, Margin = new Padding(3, 6, 3, 3) };
            _passwordTextBox = new KryptonTextBox
            {
                Dock = DockStyle.Fill,
                PasswordChar = '*',
                Margin = new Padding(3, 3, 3, 3)
            };
            layout.Controls.Add(passwordLabel, 0, 2);
            layout.Controls.Add(_passwordTextBox, 1, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                Height = 48,
                Padding = new Padding(16, 6, 16, 10)
            };

            var ok = new KryptonButton { Text = "OK", AutoSize = false, Size = new Size(90, 30), Margin = new Padding(8, 0, 0, 0) };
            ok.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(SqlUser))
                {
                    KryptonMessageBox.Show(this, "Enter a SQL login name.", "SQL authentication",
                        KryptonMessageBoxButtons.OK, KryptonMessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            var cancel = new KryptonButton { Text = "Cancel", AutoSize = false, Size = new Size(90, 30) };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            Controls.Add(layout);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }
}
