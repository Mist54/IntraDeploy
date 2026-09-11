using System;

namespace IntraDeploy.Models
{
    /// <summary>
    /// Progress information raised by DeploymentService for each pipeline step.
    /// </summary>
    public class ProgressEventArgs : EventArgs
    {
        public ProgressEventArgs(DeploymentStep step, string message, int percent, bool isIndeterminate)
        {
            Step = step;
            Message = message;
            Percent = percent;
            IsIndeterminate = isIndeterminate;
        }

        /// <summary>Current pipeline step.</summary>
        public DeploymentStep Step { get; private set; }

        /// <summary>Human readable status line.</summary>
        public string Message { get; private set; }

        /// <summary>Suggested progress bar percentage (0-100); ignored when IsIndeterminate is true.</summary>
        public int Percent { get; private set; }

        /// <summary>True for steps without a measurable duration (copying, restore).</summary>
        public bool IsIndeterminate { get; private set; }
    }
}
