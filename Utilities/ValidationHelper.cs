using System;
using System.IO;
using System.Text.RegularExpressions;

namespace IntraDeploy.Utilities
{
    /// <summary>
    /// Input validation shared by the UI and DeploymentService.
    /// </summary>
    public static class ValidationHelper
    {
        // File-system safe names. Anchored, so ".." style traversal is rejected by construction.
        private static readonly Regex SafeNameRegex = new Regex(
            @"^[A-Za-z0-9][A-Za-z0-9 ._-]*$",
            RegexOptions.Compiled);

        // IIS application path: must start with "/", no empty segments, no trailing slash.
        private static readonly Regex ApplicationPathRegex = new Regex(
            @"^/(?:[A-Za-z0-9][A-Za-z0-9 ._-]*(?:/[A-Za-z0-9][A-Za-z0-9 ._-]*)*)?$",
            RegexOptions.Compiled);

        /// <summary>True when the name is a safe folder/IIS name (no path or illegal characters).</summary>
        public static bool IsSafeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            // Trailing dot/space checks run on the RAW value: Win32 strips trailing dots and
            // spaces from paths, so "1.0." would silently collide with "1.0".
            if (value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal))
            {
                return false;
            }
            value = value.Trim();
            if (value.Length > 100 || value.Contains("\\") || value.Contains("/") || value.Contains(":"))
            {
                return false;
            }
            return SafeNameRegex.IsMatch(value);
        }

        /// <summary>True when the version string is file-system safe (digits, dots, letters, dash).</summary>
        public static bool IsSafeVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            if (value.EndsWith(".", StringComparison.Ordinal) || value.EndsWith(" ", StringComparison.Ordinal))
            {
                return false;
            }
            value = value.Trim();
            return value.Length <= 50 && SafeNameRegex.IsMatch(value);
        }

        /// <summary>
        /// True when the value is a valid IIS application path such as "/BiCore" or
        /// "/Apps/BiCore". Rejects empty strings (use "/" only programmatically), backslashes,
        /// ".." segments, trailing slashes and trailing dots/spaces per segment.
        /// </summary>
        public static bool IsSafeApplicationPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            value = value.Trim();
            if (value.Length > 200)
            {
                return false;
            }
            if (value == "/")
            {
                // Path "/" means the parent site's root application; deploying there would
                // replace the parent site's own content. Require an explicit sub-path.
                return false;
            }
            if (!ApplicationPathRegex.IsMatch(value))
            {
                return false;
            }
            foreach (string segment in value.Split('/'))
            {
                if (segment.Length == 0)
                {
                    continue; // leading slash produces one empty segment
                }
                if (segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>True when the value is a valid 1-65535 TCP port.</summary>
        public static bool IsValidPort(string value, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value.Trim(), out port))
            {
                return false;
            }
            return port >= 1 && port <= 65535;
        }

        /// <summary>True when the path exists as a directory.</summary>
        public static bool DirectoryExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }

        /// <summary>True when the path exists as a file.</summary>
        public static bool FileExists(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }
}
