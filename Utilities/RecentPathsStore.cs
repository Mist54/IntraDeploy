using System;
using System.Collections.Generic;
using System.IO;

namespace IntraDeploy.Utilities
{
    /// <summary>
    /// Simple key/value store for operator convenience state (last used source folder,
    /// last used backup folder, deployment root). Stored as plain "key=value" lines in
    /// %LOCALAPPDATA%\IntraDeploy\uistate.ini. Deliberately NOT encrypted and never
    /// holding credentials - only paths. Silent best effort: a missing or corrupt file
    /// simply means "no remembered values".
    /// </summary>
    public static class RecentPathsStore
    {
        private static readonly string StorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IntraDeploy",
            "uistate.ini");

        /// <summary>Returns the remembered value for <paramref name="key"/>, or null.</summary>
        public static string Get(string key)
        {
            try
            {
                if (!File.Exists(StorePath))
                {
                    return null;
                }
                string prefix = key + "=";
                foreach (string line in File.ReadAllLines(StorePath))
                {
                    if (line.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return line.Substring(prefix.Length);
                    }
                }
                return null;
            }
            catch (Exception)
            {
                return null; // Convenience state only; never fail the app over it.
            }
        }

        /// <summary>Sets the remembered value for <paramref name="key"/>. Best effort.</summary>
        public static void Set(string key, string value)
        {
            try
            {
                string directory = Path.GetDirectoryName(StorePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var lines = new List<string>();
                if (File.Exists(StorePath))
                {
                    lines.AddRange(File.ReadAllLines(StorePath));
                }

                string prefix = key + "=";
                int existing = lines.FindIndex(l => l.StartsWith(prefix, StringComparison.Ordinal));
                if (existing >= 0)
                {
                    lines[existing] = prefix + value;
                }
                else
                {
                    lines.Add(prefix + value);
                }

                File.WriteAllLines(StorePath, lines);
            }
            catch (Exception)
            {
                // Convenience state only; never fail the app over it.
            }
        }
    }
}
