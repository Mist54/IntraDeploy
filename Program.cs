using System;
using System.Threading;
using System.Windows.Forms;
using Krypton.Toolkit;
using IntraDeploy.Logging;
using IntraDeploy.UI;
using IntraDeploy.Utilities;
using Serilog;

namespace IntraDeploy
{
    internal static class Program
    {
        private static Mutex _singleInstanceMutex;

        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Logger.Initialize();

            // Only one IntraDeploy at a time — avoids two operators changing IIS together.
            _singleInstanceMutex = new Mutex(true, @"Local\IntraDeploySingleInstance", out bool createdNew);
            if (!createdNew)
            {
                KryptonMessageBox.Show(
                    null,
                    "IntraDeploy is already running.",
                    "IntraDeploy",
                    KryptonMessageBoxButtons.OK,
                    KryptonMessageBoxIcon.Information);
                return;
            }

            if (!ElevationHelper.IsElevated)
            {
                // Warn in the log; MainForm.CheckElevation also offers a UAC restart.
                Log.Warning("IntraDeploy started without administrator privileges; IIS operations will fail.");
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (s, e) =>
                Log.Error(e.Exception, "Unhandled UI thread exception.");
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Error(e.ExceptionObject as Exception, "Unhandled application exception.");

            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                Logger.Shutdown();
                _singleInstanceMutex.ReleaseMutex();
            }
        }
    }
}
