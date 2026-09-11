using System;

namespace IntraDeploy.Models
{
    /// <summary>LocalMachine certificate summary for HTTPS binding selection.</summary>
    public class CertificateInfo
    {
        public string Thumbprint { get; set; }
        public string Subject { get; set; }
        public string FriendlyName { get; set; }
        public DateTime NotAfter { get; set; }

        /// <summary>Operator-facing combo display: friendly/subject + short thumbprint + expiry.</summary>
        public string DisplayText
        {
            get
            {
                string name = !string.IsNullOrWhiteSpace(FriendlyName)
                    ? FriendlyName.Trim()
                    : (Subject ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "(no subject)";
                }
                string tip = string.IsNullOrEmpty(Thumbprint) || Thumbprint.Length < 8
                    ? Thumbprint
                    : Thumbprint.Substring(Thumbprint.Length - 8);
                return name + "  …" + tip + "  (exp " + NotAfter.ToString("yyyy-MM-dd") + ")";
            }
        }

        public override string ToString()
        {
            return DisplayText;
        }
    }
}
