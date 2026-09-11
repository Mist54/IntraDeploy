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
    /// <summary>
    /// Deployment dashboard. Collects operator input, checks live IIS/SQL state, shows the
    /// confirmation dialog and drives DeploymentService. All IIS/SQL/file logic lives in
    /// the Services layer.
    /// </summary>
    public partial class MainForm : KryptonForm
    {
        private readonly DeploymentService _deploymentService;
        private readonly ApplicationDetector _applicationDetector;
        private readonly IisService _iisService;
        private readonly DatabaseService _databaseService;
        private readonly PreflightService _preflightService;

        private KryptonStatusStrip _statusBar;
        private ToolStripStatusLabel _statusLabel;
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isDeploying;
        private ToolTip _tooltip;

        // Mode-specific controls (created in BuildLayout).
        private KryptonRadioButton _appUnderSiteRadio;
        private KryptonRadioButton _separateSiteRadio;
        private KryptonComboBox _parentSiteCombo;
        private KryptonTextBox _applicationPathTextBox;

        // Database auth + connection-string controls (created in BuildLayout).
        private KryptonRadioButton _windowsAuthRadio;
        private KryptonRadioButton _sqlAuthRadio;
        private KryptonButton _setSqlCredentialsButton;
        private KryptonCheckBox _updateConnectionStringCheck;
        private KryptonTextBox _connectionStringTextBox;
        private KryptonButton _buildConnectionStringButton;
        private KryptonButton _validateButton;
        private KryptonButton _rollbackButton;
        private KryptonButton _recentButton;
        private KryptonLabel _portSoftWarnLabel;
        private KryptonTextBox _healthCheckPathTextBox;
        private KryptonTextBox _httpsPortTextBox;
        private KryptonComboBox _httpsCertCombo;
        private KryptonComboBox _deployModeCombo;
        private KryptonCheckBox _dryRunCheck;

        // Session-only SQL credentials â€” never written to RecentPathsStore / ini.
        private string _sessionSqlUser;
        private string _sessionSqlPassword;

        // Keys used by RecentPathsStore to remember operator convenience state.
        private const string RecentKeySourceFolder = "LastSourceFolder";
        private const string RecentKeyBackupFolder = "LastBackupFolder";
        private const string RecentKeyDeploymentRoot = "LastDeploymentRoot";

        public MainForm()
        {
            InitializeComponent();

            _applicationDetector = new ApplicationDetector();
            _iisService = new IisService();
            _databaseService = new DatabaseService();
            _preflightService = new PreflightService(_applicationDetector, _iisService, _databaseService);
            _deploymentService = new DeploymentService(
                _applicationDetector, new FileService(), _iisService, _databaseService, new ConfigurationService());
            _deploymentService.Progress += OnDeploymentProgress;
        }

        // ------------------------------------------------------------------
        // Layout / init
        // ------------------------------------------------------------------

        private void OnFormLoad(object sender, EventArgs e)
        {
            BuildLayout();
            WireEvents();
            LoadDefaults();
            CheckElevation();
        }

        // ------------------------------------------------------------------
        // Shared UI helpers / lifetime
        // ------------------------------------------------------------------

        private void SetStatus(string message, bool busy)
        {
            if (_statusLabel != null)
            {
                _statusLabel.Text = message;
            }
            UseWaitCursor = busy;
        }

        private void SetTooltip(Control control, string text)
        {
            if (_tooltip != null && control != null)
            {
                _tooltip.SetToolTip(control, text);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isDeploying)
            {
                DialogResult choice = KryptonMessageBox.Show(
                    this,
                    "A deployment is running. Cancel it and close IntraDeploy?",
                    "Deployment in progress",
                    KryptonMessageBoxButtons.YesNo,
                    KryptonMessageBoxIcon.Question,
                    KryptonMessageBoxDefaultButton.Button2);
                if (choice != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cancellationTokenSource?.Cancel();
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _sessionSqlUser = null;
            _sessionSqlPassword = null;
            _tooltip?.Dispose();
            _tooltip = null;
            base.OnFormClosed(e);
        }
    }
}
