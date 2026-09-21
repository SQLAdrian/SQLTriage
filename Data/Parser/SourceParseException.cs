/* In the name of God, the Merciful, the Compassionate */

using System;

namespace SQLTriage.Data.Parser
{
    /// <summary>
    /// Thrown by any B2 parser component on fail-fast invariant violation.
    /// Carries the originating file path so the CheckRepositoryService
    /// boundary can surface a precise LoadError banner (doctrine #8).
    /// </summary>
    public sealed class SourceParseException : Exception
    {
        public string FilePath { get; }
        public string? CheckId { get; }

        public SourceParseException(string filePath, string message, Exception? inner = null)
            : base($"{filePath}: {message}", inner)
        {
            FilePath = filePath;
        }

        public SourceParseException(string filePath, string checkId, string message, Exception? inner = null)
            : base($"{filePath} ({checkId}): {message}", inner)
        {
            FilePath = filePath;
            CheckId = checkId;
        }
    }
}
