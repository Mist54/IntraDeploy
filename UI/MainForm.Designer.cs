namespace IntraDeploy.UI
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.kryptonManager = new Krypton.Toolkit.KryptonManager();
            this.rootPanel = new Krypton.Toolkit.KryptonPanel();
            this.headerLabel = new Krypton.Toolkit.KryptonLabel();
            this.headerSubtitle = new Krypton.Toolkit.KryptonLabel();
            this.applicationGroup = new Krypton.Toolkit.KryptonGroupBox();
            this.applicationLabel = new Krypton.Toolkit.KryptonLabel();
            this.applicationNameCombo = new Krypton.Toolkit.KryptonComboBox();
            this.versionLabel = new Krypton.Toolkit.KryptonLabel();
            this.versionTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.sourceFolderLabel = new Krypton.Toolkit.KryptonLabel();
            this.sourceFolderTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.browseSourceButton = new Krypton.Toolkit.KryptonButton();
            this.detectedTypeLabel = new Krypton.Toolkit.KryptonLabel();
            this.databaseGroup = new Krypton.Toolkit.KryptonGroupBox();
            this.useExistingRadio = new Krypton.Toolkit.KryptonRadioButton();
            this.restoreFromBakRadio = new Krypton.Toolkit.KryptonRadioButton();
            this.databaseNameLabel = new Krypton.Toolkit.KryptonLabel();
            this.databaseNameTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.backupFileLabel = new Krypton.Toolkit.KryptonLabel();
            this.backupFileTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.browseBackupButton = new Krypton.Toolkit.KryptonButton();
            this.sqlServerLabel = new Krypton.Toolkit.KryptonLabel();
            this.sqlServerTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.iisGroup = new Krypton.Toolkit.KryptonGroupBox();
            this.siteNameLabel = new Krypton.Toolkit.KryptonLabel();
            this.siteNameTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.poolNameLabel = new Krypton.Toolkit.KryptonLabel();
            this.poolNameTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.portLabel = new Krypton.Toolkit.KryptonLabel();
            this.portTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.deploymentRootLabel = new Krypton.Toolkit.KryptonLabel();
            this.deploymentRootTextBox = new Krypton.Toolkit.KryptonTextBox();
            this.browseRootButton = new Krypton.Toolkit.KryptonButton();
            this.deployButton = new Krypton.Toolkit.KryptonButton();
            this.cancelDeployButton = new Krypton.Toolkit.KryptonButton();
            ((System.ComponentModel.ISupportInitialize)(this.rootPanel)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.applicationGroup)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.databaseGroup)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.iisGroup)).BeginInit();
            this.SuspendLayout();
            //
            // kryptonManager
            //
            this.kryptonManager.GlobalPaletteMode = Krypton.Toolkit.PaletteMode.Microsoft365Blue;
            //
            // rootPanel
            //
            this.rootPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rootPanel.Location = new System.Drawing.Point(0, 0);
            this.rootPanel.Name = "rootPanel";
            this.rootPanel.Size = new System.Drawing.Size(560, 700);
            //
            // headerLabel / headerSubtitle — kept for Designer identity; hidden in BuildLayout
            //
            this.headerLabel.LabelStyle = Krypton.Toolkit.LabelStyle.TitlePanel;
            this.headerLabel.Text = "IntraDeploy";
            this.headerLabel.AutoSize = true;
            this.headerLabel.Visible = false;
            this.headerSubtitle.LabelStyle = Krypton.Toolkit.LabelStyle.NormalPanel;
            this.headerSubtitle.Text = "Internal Application Deployment";
            this.headerSubtitle.AutoSize = true;
            this.headerSubtitle.Visible = false;
            //
            // applicationGroup
            //
            this.applicationGroup.Text = "Application";
            this.applicationGroup.AutoSize = true;
            this.applicationGroup.Dock = System.Windows.Forms.DockStyle.Fill;
            this.applicationGroup.CaptionStyle = Krypton.Toolkit.LabelStyle.TitleControl;
            this.applicationGroup.GroupBorderStyle = Krypton.Toolkit.PaletteBorderStyle.ControlClient;
            this.applicationGroup.GroupBackStyle = Krypton.Toolkit.PaletteBackStyle.ControlClient;
            //
            // databaseGroup
            //
            this.databaseGroup.Text = "Database";
            this.databaseGroup.AutoSize = true;
            this.databaseGroup.Dock = System.Windows.Forms.DockStyle.Fill;
            this.databaseGroup.CaptionStyle = Krypton.Toolkit.LabelStyle.TitleControl;
            this.databaseGroup.GroupBorderStyle = Krypton.Toolkit.PaletteBorderStyle.ControlClient;
            this.databaseGroup.GroupBackStyle = Krypton.Toolkit.PaletteBackStyle.ControlClient;
            //
            // iisGroup
            //
            this.iisGroup.Text = "IIS";
            this.iisGroup.AutoSize = true;
            this.iisGroup.Dock = System.Windows.Forms.DockStyle.Fill;
            this.iisGroup.CaptionStyle = Krypton.Toolkit.LabelStyle.TitleControl;
            this.iisGroup.GroupBorderStyle = Krypton.Toolkit.PaletteBorderStyle.ControlClient;
            this.iisGroup.GroupBackStyle = Krypton.Toolkit.PaletteBackStyle.ControlClient;
            //
            // deployButton / cancelDeployButton
            //
            this.deployButton.Text = "DEPLOY";
            this.deployButton.AutoSize = true;
            this.cancelDeployButton.Text = "Cancel";
            this.cancelDeployButton.AutoSize = true;
            this.cancelDeployButton.Enabled = false;
            //
            // MainForm — compact portrait (~3:4)
            //
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(560, 900);
            this.MinimumSize = new System.Drawing.Size(520, 780);
            this.Text = "IntraDeploy — Internal Application Deployment";
            this.Icon = IntraDeploy.UI.Branding.AppIcon;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.Controls.Add(this.rootPanel);
            this.Name = "MainForm";
            this.Load += new System.EventHandler(this.OnFormLoad);
            ((System.ComponentModel.ISupportInitialize)(this.rootPanel)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.applicationGroup)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.databaseGroup)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.iisGroup)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private Krypton.Toolkit.KryptonManager kryptonManager;
        private Krypton.Toolkit.KryptonPanel rootPanel;
        private Krypton.Toolkit.KryptonLabel headerLabel;
        private Krypton.Toolkit.KryptonLabel headerSubtitle;
        private Krypton.Toolkit.KryptonGroupBox applicationGroup;
        private Krypton.Toolkit.KryptonLabel applicationLabel;
        private Krypton.Toolkit.KryptonComboBox applicationNameCombo;
        private Krypton.Toolkit.KryptonLabel versionLabel;
        private Krypton.Toolkit.KryptonTextBox versionTextBox;
        private Krypton.Toolkit.KryptonLabel sourceFolderLabel;
        private Krypton.Toolkit.KryptonTextBox sourceFolderTextBox;
        private Krypton.Toolkit.KryptonButton browseSourceButton;
        private Krypton.Toolkit.KryptonLabel detectedTypeLabel;
        private Krypton.Toolkit.KryptonGroupBox databaseGroup;
        private Krypton.Toolkit.KryptonRadioButton useExistingRadio;
        private Krypton.Toolkit.KryptonRadioButton restoreFromBakRadio;
        private Krypton.Toolkit.KryptonLabel databaseNameLabel;
        private Krypton.Toolkit.KryptonTextBox databaseNameTextBox;
        private Krypton.Toolkit.KryptonLabel backupFileLabel;
        private Krypton.Toolkit.KryptonTextBox backupFileTextBox;
        private Krypton.Toolkit.KryptonButton browseBackupButton;
        private Krypton.Toolkit.KryptonLabel sqlServerLabel;
        private Krypton.Toolkit.KryptonTextBox sqlServerTextBox;
        private Krypton.Toolkit.KryptonGroupBox iisGroup;
        private Krypton.Toolkit.KryptonLabel siteNameLabel;
        private Krypton.Toolkit.KryptonTextBox siteNameTextBox;
        private Krypton.Toolkit.KryptonLabel poolNameLabel;
        private Krypton.Toolkit.KryptonTextBox poolNameTextBox;
        private Krypton.Toolkit.KryptonLabel portLabel;
        private Krypton.Toolkit.KryptonTextBox portTextBox;
        private Krypton.Toolkit.KryptonLabel deploymentRootLabel;
        private Krypton.Toolkit.KryptonTextBox deploymentRootTextBox;
        private Krypton.Toolkit.KryptonButton browseRootButton;
        private Krypton.Toolkit.KryptonButton deployButton;
        private Krypton.Toolkit.KryptonButton cancelDeployButton;
    }
}
