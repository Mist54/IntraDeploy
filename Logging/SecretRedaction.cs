using System;

namespace IntraDeploy.Logging
{
    /// <summary>
    /// Detects shapes that look like secrets in strings (for tests and call-site checks).
    /// Not a runtime Serilog filter — avoid logging connection strings / passwords at all.
    /// </summary>
    public static class SecretRedaction
    {
        /// <summary>
        /// True when <paramref name="text"/> looks like it contains a password or connection-string secret.
        /// </summary>
        public static bool ContainsSecretShape(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            if (text.IndexOf("Password=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (text.IndexOf("Pwd=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (text.IndexOf("User ID=", StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("Initial Catalog=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Full SQL connection string shape (often includes password when not integrated).
                return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="haystack"/> contains <paramref name="needle"/> (ordinal).
        /// Used by SmokeTest to assert distinctive fake secrets never appear in summaries.
        /// </summary>
        public static bool ContainsLiteral(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            {
                return false;
            }
            return haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
        }
    }
}
