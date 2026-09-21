/* In the name of God, the Merciful, the Compassionate */

#nullable enable

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SQLTriage.Data.Models
{
    /// <summary>
    /// The SEAT IDENTITY for instance-seat licensing (ruled by Adrian, 2026-07-17).
    ///
    /// A fingerprint is a SHA-256 over the canonical string:
    ///     lower(MachineName) + "|" + lower(InstanceName ?? "MSSQLSERVER") + "|" + MasterCreateDate("O")
    ///
    /// WHY THESE THREE FIELDS:
    ///   • master's create_date is immutable per install and distinct per named instance on the
    ///     same host — that is what makes a REPOINT cost a seat.
    ///   • MachineName + InstanceName pin the identity to a physical install.
    ///   • ServerName and the operator's alias are DISPLAY metadata ONLY and are deliberately NOT
    ///     hashed: a server RENAME must not burn a swap, but repointing an alias at a DIFFERENT
    ///     box must. Putting ServerName in the hash would invert both of those.
    ///
    /// KNOWN, ACCEPTED LOOPHOLE — documented honestly, not papered over: a CLONED or RESTORED VM
    /// carries the same MachineName + master create_date and therefore SHARES a seat. Two cloned
    /// VMs are one seat as far as this scheme is concerned. Closing it would need something
    /// host-side (hardware id / TPM), which this read-only-one-round-trip design deliberately does
    /// not reach for. Do not pretend otherwise in UI copy.
    ///
    /// This type is PURE (no SQL, no I/O) so the hash contract is unit-testable without a server.
    /// The probe that populates it lives in InstanceFingerprintProbe.
    /// </summary>
    public sealed record InstanceFingerprint
    {
        /// <summary>The default-instance sentinel. SERVERPROPERTY('InstanceName') is NULL on a
        /// default instance; every real default instance must hash to the same canonical token.</summary>
        public const string DefaultInstanceName = "MSSQLSERVER";

        /// <summary>SERVERPROPERTY('MachineName'). Part of the hash (lowercased).</summary>
        public required string MachineName { get; init; }

        /// <summary>SERVERPROPERTY('InstanceName'), or null on a default instance. Part of the hash
        /// (lowercased, null collapsing to <see cref="DefaultInstanceName"/>).</summary>
        public string? InstanceName { get; init; }

        /// <summary>master's create_date. Part of the hash, round-trip ("O") formatted.</summary>
        public required DateTime MasterCreateDate { get; init; }

        /// <summary>SERVERPROPERTY('ServerName') — DISPLAY ONLY, deliberately NOT hashed.</summary>
        public string? ServerName { get; init; }

        /// <summary>
        /// The canonical UTF-8 string that gets hashed. Exposed for diagnostics and tests.
        ///
        /// Culture-invariant by construction: ToLowerInvariant (never ToLower — a Turkish locale
        /// would map 'I' to a dotless i and silently fork the fingerprint of the SAME instance),
        /// and "O" formatting with InvariantCulture (round-trip, so the bytes never depend on the
        /// host's date format or DateTimeKind).
        /// </summary>
        public string Canonical =>
            MachineName.ToLowerInvariant() + "|" +
            (string.IsNullOrWhiteSpace(InstanceName) ? DefaultInstanceName : InstanceName).ToLowerInvariant() + "|" +
            MasterCreateDate.ToString("O", CultureInfo.InvariantCulture);

        /// <summary>
        /// The seat identity: lowercase-hex SHA-256 of <see cref="Canonical"/>.
        /// Stable: same input => same hash, forever. This value is persisted in the seat register
        /// and joined to results, so ANY change to the canonical form is a breaking migration.
        /// </summary>
        public string Hash =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical))).ToLowerInvariant();

        /// <summary>
        /// Short, human-readable id for logs and the seat table (first 12 hex chars). Collision-safe
        /// enough for display at estate scale; NEVER use it as the key — <see cref="Hash"/> is the key.
        /// </summary>
        public string ShortHash => Hash[..12];

        /// <summary>
        /// Best display label for this instance: the server's own ServerName when it reported one,
        /// else MachineName\InstanceName. Never participates in the hash.
        /// </summary>
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(ServerName)
                ? ServerName!
                : string.IsNullOrWhiteSpace(InstanceName)
                    ? MachineName
                    : MachineName + "\\" + InstanceName;
    }
}
