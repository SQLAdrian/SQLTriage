/* In the name of God, the Merciful, the Compassionate */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;


#pragma warning disable CA1416 // Windows-only API — project targets net8.0-windows
namespace SQLTriage.Data
{
    /// <summary>
    /// Encrypts and decrypts sensitive strings using layered encryption:
    ///   "enc:"  — DPAPI CurrentUser scope (interactive user, original)
    ///   "aes:"  — AES-256-GCM with DPAPI-protected machine key (works for services + any user)
    ///
    /// Decryption tries all formats automatically.
    /// New encryptions use AES-256-GCM by default (strongest, cross-account on same machine).
    ///
    /// <para>⚠ REVIEWED 2026-08-20 for the C1 DPAPI-scope ruling (move wraps to CurrentUser with a
    /// LocalMachine read-fallback, but only for stores one account both writes and reads).
    /// <c>.credential-key</c> stays LocalMachine: it is resolved at
    /// <c>Path.Combine(AppContext.BaseDirectory, "config", ".credential-key")</c> — the shared
    /// install folder, not a per-user profile path — and <c>AddSharedServices</c> wires this class
    /// into BOTH hosts (<c>App.xaml.cs</c> for the interactive desktop app, <c>WindowsServiceHost.cs</c>
    /// for the installed service), which normally run as two different Windows accounts against the
    /// SAME install. A server password saved by an operator in the desktop app must still decrypt
    /// for a scheduled scan running as <c>NT SERVICE\SQLTriage</c>, and DPAPI CurrentUser data is
    /// bound to the SID that wrapped it — a CurrentUser write here would make that cross-account read
    /// fail outright, not just add a fallback. This is a genuinely SHARED store; see
    /// WORKLIST-2026-08-20-app-lane-wave.md needsRuling for the consolidated finding across all four
    /// LocalMachine-DPAPI sites.</para>
    /// </summary>
    public static class CredentialProtector
    {
        private static readonly byte[] AppEntropy =
            Encoding.UTF8.GetBytes("SQLTriage.SQLTriage.v1");

        // AES key file location — next to the exe, protected by NTFS + DPAPI machine scope
        private static readonly string KeyFilePath =
            Path.Combine(AppContext.BaseDirectory, "config", ".credential-key");

        // ────────────────────────────────────────────────────────────────
        //  Public API
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypts a plaintext string using AES-256-GCM (preferred) with fallback to DPAPI.
        /// </summary>
        public static string Encrypt(string? plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                return EncryptAesGcm(plainText);
            }
            catch (KeyAsideRefusedException refusal)
            {
                // The machine-scope credential key was deliberately NOT replaced (posture (b) or
                // (c), RULED 2026-08-10), so there is no AES key to seal under and the DPAPI
                // fallback below is the only way this save succeeds at all.
                //
                // MEASURED 2026-08-10, by planting a foreign-entropy .credential-key and blockers at
                // every aside name in a copy of the test host: Encrypt returns a value that starts
                // "enc:", not "aes:". That is a SCOPE CHANGE, not a format detail. "aes:" is
                // AES-256-GCM under a DPAPI LocalMachine key, readable by any account on this
                // machine including the installed Windows service; "enc:" is DPAPI CurrentUser,
                // readable only by the account that wrote it. SQLTriage runs both ways, and Decrypt
                // answers the empty string for a value it cannot open, saying nothing at the point
                // of failure. So a transient lock on the credential key could otherwise leave a
                // permanently unreadable saved password with no signal anywhere. It is stated here,
                // at the moment it happens, because nothing else states it.
                //
                // The save is kept rather than refused on purpose: refusing loses the password the
                // operator just typed, and this method's answer for a total failure is the empty
                // string, which the caller stores as a blank password. This route into the fallback
                // is new with the ruling. Before it, a failed aside logged and regenerated, so
                // EncryptAesGcm succeeded and the value stayed "aes:".
                Serilog.Log.Error(refusal,
                    "[CredentialProtector] The credential key was deliberately not replaced, so this "
                    + "credential is being saved under DPAPI for the Windows account running "
                    + "SQLTriage right now instead of under the machine-scope key. It is encrypted, "
                    + "and it will read back empty for every other account, including the SQLTriage "
                    + "Windows service. Fix the key file named in the refusal above, then save this "
                    + "credential again so it is re-encrypted machine-scope.");

                try { return EncryptDpapi(plainText, DataProtectionScope.CurrentUser, "enc:"); }
                catch (Exception dpapiEx)
                {
                    Serilog.Log.Error(dpapiEx, "[CredentialProtector] The machine-scope key was refused and DPAPI also failed, so the credential was NOT saved");
                    return string.Empty;
                }
            }
            catch (Exception aesEx)
            {
                Serilog.Log.Warning(aesEx, "[CredentialProtector] AES-GCM encryption failed, falling back to DPAPI");
                // Fallback to DPAPI CurrentUser if AES fails
                try { return EncryptDpapi(plainText, DataProtectionScope.CurrentUser, "enc:"); }
                catch (Exception dpapiEx)
                {
                    Serilog.Log.Error(dpapiEx, "[CredentialProtector] Both AES-GCM and DPAPI encryption failed — credential will NOT be saved");
                    return string.Empty;
                }
            }
        }

        /// <summary>
        /// Decrypts a string. Auto-detects format: aes:, enc:, or legacy plaintext.
        /// </summary>
        public static string Decrypt(string? encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return string.Empty;

            // AES-256-GCM (preferred)
            if (encryptedText.StartsWith("aes:", StringComparison.Ordinal))
                return DecryptAesGcm(encryptedText);

            // DPAPI CurrentUser (legacy)
            if (encryptedText.StartsWith("enc:", StringComparison.Ordinal))
                return DecryptDpapi(encryptedText, "enc:");

            // Legacy plaintext — will be re-encrypted on next save
            Serilog.Log.Warning("Legacy plaintext credential detected — will be re-encrypted on next save");
            return encryptedText;
        }

        /// <summary>
        /// Returns true if the value is already encrypted (any supported format).
        /// </summary>
        public static bool IsEncrypted(string? value)
        {
            return value != null && (
                value.StartsWith("enc:", StringComparison.Ordinal) ||
                value.StartsWith("aes:", StringComparison.Ordinal));
        }

        // ────────────────────────────────────────────────────────────────
        //  AES-256-GCM  (96-bit nonce, 128-bit tag, 256-bit key)
        // ────────────────────────────────────────────────────────────────

        private static string EncryptAesGcm(string plainText)
        {
            var key = GetOrCreateAesKey();
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var result = AesGcmHelper.Encrypt(plainBytes, key);
            return "aes:" + Convert.ToBase64String(result);
        }

        private static string DecryptAesGcm(string encryptedText)
        {
            try
            {
                var key = GetOrCreateAesKey();
                var data = Convert.FromBase64String(encryptedText.Substring(4));
                var plainBytes = AesGcmHelper.Decrypt(data, key);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch (CryptographicException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] AES-GCM decryption failed — credential may be corrupted or tampered with");
                return string.Empty;
            }
            catch (FormatException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] AES-GCM decryption failed — invalid Base64 format");
                return string.Empty;
            }
            catch (KeyAsideRefusedException ex)
            {
                // The key was NOT replaced, on purpose (posture (b) or (c), RULED 2026-08-10), so
                // there is no key to decrypt with and this value cannot be read. The empty string is
                // this method's existing answer for "cannot decrypt", and it is the honest one here:
                // the value is intact on disk and readable again the moment the key file is. The
                // reason was already logged at Error where the refusal was raised, with the path and
                // the recovery step; this line records which credential read it stopped.
                Serilog.Log.Error(ex,
                    "[CredentialProtector] A saved credential could not be decrypted because the "
                    + "credential key was deliberately not replaced. See the refusal logged above.");
                return string.Empty;
            }
        }

        /// <summary>
        /// Gets or creates a 256-bit AES key, stored on disk protected by DPAPI LocalMachine scope.
        /// This allows any process on the machine to use the key (including Windows Services).
        /// </summary>
        private static byte[] GetOrCreateAesKey()
        {
            var regenerating = false;

            if (File.Exists(KeyFilePath))
            {
                try
                {
                    var protectedKey = File.ReadAllBytes(KeyFilePath);

                    // CENSUSED 2026-08-10 (posture (a)): there is NO length gate on this return, and
                    // none was added. The wrong-length ruling is about branches that regenerate over
                    // material DPAPI handed back; this branch has none to make safe, because it uses
                    // whatever length it gets. Adding a gate here would create the destructive branch
                    // the ruling exists to remove. What a wrong-length key does INSTEAD is measured by
                    // CredentialProtectorKeyPostureTests: AES-GCM refuses a non-AES length at the
                    // point of use, and accepts 16 or 24 bytes as a shorter AES key. This build only
                    // ever mints 32, and pinning the length at the point of use is a ruling nobody
                    // has taken.
                    return ProtectedData.Unprotect(protectedKey, AppEntropy, DataProtectionScope.LocalMachine);
                }
                catch (CryptographicException ex)
                {
                    // MED (2026-07-07): the key file EXISTS but won't unwrap. Overwriting it makes
                    // every stored 'aes:' credential undecryptable forever, silently (Decrypt just
                    // returns empty). Preserve it aside so a transient DPAPI fault is recoverable,
                    // and log LOUDLY — never fail silently on a data-affecting event.
                    Serilog.Log.Error(ex, "[CredentialProtector] Could not unwrap credential AES key at {Path}; preserving aside and regenerating (saved passwords will need re-entry)", KeyFilePath);
                    RefuseUnlessPreserved(KeyAsideLifecycle.SetAside(KeyFilePath, AsideLog));
                    regenerating = true;
                }
            }

            // Generate new 256-bit key
            var newKey = new byte[32];
            RandomNumberGenerator.Fill(newKey);

            // Protect with DPAPI LocalMachine scope (any user/service on this machine can decrypt)
            var protectedBytes = ProtectedData.Protect(newKey, AppEntropy, DataProtectionScope.LocalMachine);

            var dir = Path.GetDirectoryName(KeyFilePath);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(KeyFilePath, protectedBytes);

            // Restrict file permissions (best-effort — NTFS ACL)
            try
            {
                var fileInfo = new FileInfo(KeyFilePath);
                fileInfo.Attributes |= FileAttributes.Hidden;
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "[CredentialProtector] Failed to set hidden attribute on key file"); }

            // The discriminate-and-prove lifecycle (HOUSE RULE 2026-08-09), and only on the path
            // that regenerated: a first-run install has nothing set aside and nothing to reconcile,
            // and this method is called on EVERY encrypt and decrypt, so a sweep on the happy path
            // would put a directory enumeration and an unwrap in front of each one. Both steps run
            // here, in this order, because the cleanup is authorised by the proof and by nothing else.
            if (regenerating)
            {
                var proof = KeyAsideLifecycle.ProveReplacement(
                    KeyFilePath, newKey, AppEntropy, DataProtectionScope.LocalMachine);

                // Reconcile BEFORE the refusal: it deletes nothing on an unproven proof (it is handed
                // the same one) and it names every preserved file in the log, which is what the
                // refusal tells the operator to go and look for.
                KeyAsideLifecycle.ReconcileAsides(
                    KeyFilePath, newKey, AppEntropy, DataProtectionScope.LocalMachine, proof, AsideLog);

                // RULED 2026-08-10: an unproven replacement does not become the key in force. Until
                // this ruling the failure was logged at Error and the key was used anyway, so a
                // password saved after it looked saved and could not be read back after a restart.
                if (!proof.Proven)
                {
                    var message = KeyAsideLifecycle.RefusalNotProven(
                        KeyFilePath,
                        "passwords saved under a key the next start cannot read would come back "
                        + "empty after that restart, with nothing said at the time they were saved",
                        proof.Detail);
                    Serilog.Log.Error("[CredentialProtector] {Message}", message);
                    throw new KeyAsideRefusedException(message);
                }
            }

            return newKey;
        }

        /// <summary>
        /// Posture (b), RULED 2026-08-10: this class does not write a fresh credential key over
        /// material it could not preserve.
        ///
        /// <para>What the caller sees follows this class's OWN error contract rather than a new one.
        /// <see cref="Encrypt"/> catches this refusal and falls back to DPAPI CurrentUser, so a
        /// credential being saved is still saved and still encrypted, AT A DIFFERENT SCOPE: "enc:"
        /// can be read only by the Windows account that wrote it, where "aes:" can be read by any
        /// account on the machine, service included. That downgrade is logged at Error where it
        /// happens, with what to do about it, because <see cref="Decrypt"/> answers the empty string
        /// for a value it cannot open and says nothing at the point of failure.
        /// <see cref="DecryptAesGcm"/> catches this refusal explicitly and returns the empty string,
        /// which is what it already does for every value it cannot decrypt. Nothing is cached, so a
        /// file that was briefly locked is retried on the next encrypt or decrypt.</para>
        /// </summary>
        private static void RefuseUnlessPreserved(KeyAsideResult aside)
        {
            if (aside.SafeToOverwrite) return;

            var message = KeyAsideLifecycle.RefusalNotPreserved(
                KeyFilePath,
                "every password already saved under it could never be decrypted again",
                aside);
            Serilog.Log.Error("[CredentialProtector] {Message}", message);
            throw new KeyAsideRefusedException(message);
        }

        /// <summary>Routes <see cref="KeyAsideLifecycle"/>'s sentences into this class's log prefix.</summary>
        private static void AsideLog(bool isError, Exception? error, string message)
        {
            if (isError) Serilog.Log.Error(error, "[CredentialProtector] {Message}", message);
            else Serilog.Log.Warning(error, "[CredentialProtector] {Message}", message);
        }

        // ────────────────────────────────────────────────────────────────
        //  DPAPI (legacy, kept for backward compatibility)
        // ────────────────────────────────────────────────────────────────

        private static string EncryptDpapi(string plainText, DataProtectionScope scope, string prefix)
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, AppEntropy, scope);
            return prefix + Convert.ToBase64String(encryptedBytes);
        }

        private static string DecryptDpapi(string encryptedText, string prefix)
        {
            try
            {
                string base64 = encryptedText.Substring(prefix.Length);
                byte[] encryptedBytes = Convert.FromBase64String(base64);

                // Try CurrentUser first (original), then LocalMachine (service mode)
                try
                {
                    byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, AppEntropy, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(decryptedBytes);
                }
                catch (CryptographicException)
                {
                    byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, AppEntropy, DataProtectionScope.LocalMachine);
                    return Encoding.UTF8.GetString(decryptedBytes);
                }
            }
            catch (CryptographicException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] DPAPI decryption failed — credential may be from a different machine or user");
                return string.Empty;
            }
            catch (FormatException ex)
            {
                Serilog.Log.Warning(ex, "[CredentialProtector] DPAPI decryption failed — invalid Base64 format, returning raw value");
                return encryptedText;
            }
        }
    }
}
#pragma warning restore CA1416
