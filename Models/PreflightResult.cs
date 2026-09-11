using System.Collections.Generic;
using System.Linq;

namespace IntraDeploy.Models
{
    /// <summary>Severity of a single pre-flight finding. Errors block a clean Validate; warnings do not.</summary>
    public enum PreflightSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>One pre-flight check outcome. Messages must never contain secrets.</summary>
    public class PreflightFinding
    {
        public PreflightSeverity Severity { get; set; }
        public string Area { get; set; }
        public string Message { get; set; }

        /// <summary>
        /// When true, Deploy must obtain an explicit operator Continue before proceeding.
        /// Stronger than a log-only warning (e.g. existing app pool CLR mismatch).
        /// </summary>
        public bool RequiresExplicitContinue { get; set; }

        public PreflightFinding(PreflightSeverity severity, string area, string message)
            : this(severity, area, message, requiresExplicitContinue: false)
        {
        }

        public PreflightFinding(
            PreflightSeverity severity,
            string area,
            string message,
            bool requiresExplicitContinue)
        {
            Severity = severity;
            Area = area;
            Message = message;
            RequiresExplicitContinue = requiresExplicitContinue;
        }
    }

    /// <summary>Aggregate result of <see cref="Services.PreflightService.Validate"/> — no writes performed.</summary>
    public class PreflightResult
    {
        public List<PreflightFinding> Findings { get; private set; }

        public PreflightResult()
        {
            Findings = new List<PreflightFinding>();
        }

        public bool HasErrors
        {
            get { return Findings.Any(f => f.Severity == PreflightSeverity.Error); }
        }

        public bool HasWarnings
        {
            get { return Findings.Any(f => f.Severity == PreflightSeverity.Warning); }
        }

        /// <summary>True when any finding requires an explicit Continue on Deploy (pool CLR gate).</summary>
        public bool HasContinueRequiredWarnings
        {
            get { return Findings.Any(f => f.RequiresExplicitContinue); }
        }

        public void Add(PreflightSeverity severity, string area, string message)
        {
            Add(severity, area, message, requiresExplicitContinue: false);
        }

        public void Add(
            PreflightSeverity severity,
            string area,
            string message,
            bool requiresExplicitContinue)
        {
            Findings.Add(new PreflightFinding(severity, area, message, requiresExplicitContinue));
        }
    }
}
