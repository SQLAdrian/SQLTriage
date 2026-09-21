/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Payload for <see cref="ScheduledTaskEngine.AssessmentRunCompleted"/>: an assessment
    /// run finished on a server, and nothing more.
    ///
    /// Ships in every build profile. By contract the type name and members stay neutral
    /// about who consumes the event — build-gated modules subscribe behind their own
    /// fences (see ServiceCollectionExtensions), and a build without a subscriber never
    /// observes it.
    /// </summary>
    public sealed class AssessmentRunCompletedEventArgs : EventArgs
    {
        /// <summary>The server the assessment run targeted.</summary>
        public string ServerName { get; init; } = "";

        /// <summary>UTC instant the run finished.</summary>
        public DateTime CompletedAtUtc { get; init; }
    }
}
