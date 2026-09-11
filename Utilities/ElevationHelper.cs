using System;
using System.Diagnostics;
using System.Security.Principal;

namespace IntraDeploy.Utilities
{
    /// <summary>
    /// Checks admin rights and can relaunch IntraDeploy elevated (UAC).
    /// IIS configuration needs administrator privileges.
    /// </summary>
    public static class ElevationHelper
    {
        /// <summary>True when this process is already running as administrator.</summary>
        public static bool IsElevated
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
        }

        /// <summary>
        /// Starts this same EXE again with "Run as administrator", then the caller should exit.
        /// Returns false if the user cancels the UAC prompt.
        /// </summary>
        public static bool RestartElevated()
        {
            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string arguments = Environment.CommandLine;
            // Keep only args after the exe path so we do not pass the path twice.
            string exeQuoted = "\"" + exePath + "\"";
            if (arguments.StartsWith(exeQuoted, StringComparison.OrdinalIgnoreCase))
            {
                arguments = arguments.Substring(exeQuoted.Length).TrimStart();
            }
            else if (arguments.StartsWith(exePath, StringComparison.OrdinalIgnoreCase))
            {
                arguments = arguments.Substring(exePath.Length).TrimStart();
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas" // triggers the Windows UAC elevation prompt
            };

            try
            {
                Process.Start(startInfo);
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User cancelled UAC — stay in the unelevated process.
                return false;
            }
        }
    }
}
