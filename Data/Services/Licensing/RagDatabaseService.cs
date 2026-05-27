/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services.Licensing
{
    /// <summary>
    /// Gates access to the RAG (retrieval-augmented generation) database file.
    ///
    /// rag.db is too large (~100MB+) to embed in the encrypted bundle. For v0.90.2
    /// distribution is manual: Adrian emails customers a OneDrive/Dropbox link;
    /// customer downloads rag.db and drops it next to sqltriage.exe (same folder
    /// as their *.aesgcm). Auto-download from sqldba.org is deferred to v0.91.
    ///
    /// This service answers two independent questions:
    ///   1. Is rag.db PRESENT on disk?  (RagFileExists)
    ///   2. Does the customer's license PERMIT RAG features?  (bundle.Features.RagEnabled)
    ///
    /// Both must be true for RAG features to function (IsEnabled). The UI consumes
    /// these flags to show the right banner:
    ///   - file missing + license permits → "Drop rag.db next to sqltriage.exe to enable RAG retrieval."
    ///   - file present + license forbids → "RAG retrieval requires a Full + RAG tier license. Contact Adrian to upgrade."
    ///   - file missing + license forbids → no RAG UI surfaced at all.
    ///   - file present + license permits → RAG features enabled; service exposes RagFilePath.
    ///
    /// DI registration TODO: register as singleton in
    /// <c>Data/ServiceCollectionExtensions.cs</c> alongside other Licensing services.
    /// Not added here because Phase 9 is editing that file in parallel.
    /// </summary>
    public sealed class RagDatabaseService
    {
        private const string DefaultRagFileName = "rag.db";

        private readonly ILogger<RagDatabaseService> _logger;
        private readonly IBundleAccessor _bundle;

        public RagDatabaseService(ILogger<RagDatabaseService> logger, IBundleAccessor bundle)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        }

        /// <summary>Absolute path where rag.db is expected (install dir, next to sqltriage.exe).</summary>
        public string RagFilePath => Path.Combine(AppContext.BaseDirectory, DefaultRagFileName);

        /// <summary>True when rag.db exists on disk.</summary>
        public bool RagFileExists => File.Exists(RagFilePath);

        /// <summary>True when the current license tier permits RAG features (independent of file presence).</summary>
        public bool LicensePermitsRag => _bundle.IsUnlocked && _bundle.Features.RagEnabled;

        /// <summary>True iff both the file exists AND the license permits use.</summary>
        public bool IsEnabled => RagFileExists && LicensePermitsRag;

        /// <summary>UX guidance for the current state. Returned as a stable enum so the UI can switch on it.</summary>
        public RagState State
        {
            get
            {
                var hasFile = RagFileExists;
                var permits = LicensePermitsRag;
                if (hasFile && permits) return RagState.Ready;
                if (!hasFile && permits) return RagState.MissingFile;
                if (hasFile && !permits) return RagState.LockedByLicense;
                return RagState.NotAvailable;
            }
        }

        /// <summary>
        /// Returns the file size of rag.db in bytes, or null if the file is missing.
        /// Used by the UI to show "downloaded 248 MB" confirmations.
        /// </summary>
        public long? RagFileSizeBytes
        {
            get
            {
                try
                {
                    return RagFileExists ? new FileInfo(RagFilePath).Length : null;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Could not stat {Path}", RagFilePath);
                    return null;
                }
            }
        }
    }

    /// <summary>UX-facing RAG availability state. Order intentionally matches "best to worst" outcome.</summary>
    public enum RagState
    {
        /// <summary>rag.db present and license permits — RAG features active.</summary>
        Ready,
        /// <summary>License permits RAG but rag.db is not on disk — prompt user to drop the file.</summary>
        MissingFile,
        /// <summary>rag.db is present but license forbids — prompt upgrade.</summary>
        LockedByLicense,
        /// <summary>Neither file nor license — no RAG UI surfaced.</summary>
        NotAvailable
    }
}
