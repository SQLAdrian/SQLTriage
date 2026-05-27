---
layout: default
title: SQLTriage — Activate Full Audit
description: How to activate the Full tier — bundle file, 24-word license key, customer name. Step-by-step install, troubleshooting, and support.
---

# SQLTriage — Activate Full Audit

SQLTriage ships with a working **Free tier** out of the box. The Free tier runs ~111 audit-first checks against your SQL Server, imports your existing sp_BLITZ CSV output, and maps findings to NIST / CIS / STIG framework controls. You can use it indefinitely with no key.

The **Full tier** unlocks the complete ~700-check audit corpus, governance weighting, build catalogue, and SQL licensing-cost estimator. Some tiers also unlock RAG-powered retrieval (semantic search over the full audit history).

This guide covers activating Full tier on your machine.

## What you received from the SQLTriage team

You should have an email containing three items:

1. **A bundle file** named like `acme-corp.aesgcm`. This is your customer-specific encrypted bundle.
2. **A 24-word license key** — a sentence of 24 lowercase English words separated by spaces. Example format (these aren't real words from your key):
   ```
   verb noun aware bind chair drift echo flag glove home iron jolt
   keep land mango next oval prize queen rare seed talk under vivid
   ```
3. **Your customer name** — the exact string used when generating your bundle. Spelling and case matter: "Acme Corp" is different from "acme corp" or "Acme Corporation".

If your tier includes RAG, the email also has a download link for `rag.db` (typically ~100–300 MB on a OneDrive or Dropbox share). RAG is optional — Full tier works without it.

## Installation

1. Install SQLTriage as normal by running the `SQLTriage-vX.Y.Z-buildNNNN-Setup.exe` installer linked in your activation email.
2. Place your `.aesgcm` bundle in the **SQLTriage install directory**, alongside `sqltriage.exe`. Default install paths:
   - Per-user (no admin required): `%LocalAppData%\Programs\SQLTriage\`
   - Machine-wide: `C:\Program Files\SQLTriage\`
3. If your tier includes RAG, drop `rag.db` in the same folder as `sqltriage.exe`.
4. Launch SQLTriage. The app boots in **Free tier** on first launch — that's expected.

## Activate Full Audit

1. Open SQLTriage. Click **Settings** in the navigation menu.
2. Find the **Activate Full Audit** card.
3. Paste your **customer name** into the *Customer name* field. Match the spelling from your activation email exactly — including capitalisation and punctuation.
4. Paste your **24-word license key** into the *License key* field. Whitespace and case don't matter; the app trims and lowercases the input before decoding.
5. Click **Activate**.

On success, a toast appears confirming activation. The tier badge in the navigation menu flips from "Free" to "Full". The full corpus loads on the next page navigation — visit **Audit Assessment** to confirm the check count jumps from ~111 to ~700.

On failure, the activation card shows a specific error. See Troubleshooting below.

## RAG (optional add-on, RAG-enabled tiers only)

If your license tier includes RAG:

- Place `rag.db` next to `sqltriage.exe`.
- RAG features (semantic search, "find similar findings" panels) become active automatically.
- If the file is missing, the app shows a banner: *"Drop rag.db next to sqltriage.exe to enable RAG retrieval."*

If your license tier does NOT include RAG:

- RAG features stay locked even if `rag.db` is present on disk.
- The app shows: *"RAG retrieval requires a Full + RAG tier license. Contact support to upgrade."*

Auto-download of `rag.db` from `sqldba.org` is planned for a future release (v0.91+). For v0.90.2, manual file placement is the only path.

## Troubleshooting

| Message you see | What it means | What to do |
|---|---|---|
| `Decryption failed — key may be corrupt or for a different build` | Your key was generated against a different SQLTriage build than the one installed. | Check **Settings → About** for the version + build number. If versions don't match, email support for a re-issued bundle. |
| `AAD mismatch — customer name doesn't match any installed bundle` | The customer name you typed doesn't match the name embedded in any `.aesgcm` file in your install dir. | Re-check spelling and case. "Acme Corp" ≠ "acme corp" ≠ "ACME CORP". Copy/paste from your activation email, don't retype. |
| `No .aesgcm files found in install dir` | The bundle file is not in the right folder. | Confirm the file lives in the same directory as `sqltriage.exe`, has a `.aesgcm` extension (not `.aesgcm.txt`), and is readable. Some browsers add a `.txt` suffix when saving attachments — rename if needed. |
| `Checksum mismatch — phrase corrupt or mistyped` | One or more words in your 24-word key has a typo. BIP39 has a built-in checksum so most single-word errors are detected. | Compare each word against your activation email. Common confusions: missing word, transposed words, swapped homophones. |
| App still shows "Free tier" after activation toast confirmed success | Bundle re-load on next boot didn't pick up the new license. | Restart SQLTriage. |

If you see an error not listed here, email support with: your customer name (as typed), the version + build number from **Settings → About**, the exact error text, and whether activation previously worked.

## Deactivating

To deactivate Full Audit on this machine:

1. **Settings → Activate Full Audit**.
2. Click **Deactivate Full Audit**.

This clears your saved customer name and DPAPI-wrapped license key from local settings. The app reverts to Free tier on next boot. The `.aesgcm` file stays on disk — you can re-activate later by re-pasting the same customer name and 24-word key.

Useful before: returning a machine, switching to a different customer account, or troubleshooting an activation issue.

## Privacy and security

- Your license key is stored locally, encrypted with Windows DPAPI (CurrentUser scope). It can only be decrypted by your Windows user account on this machine. Copying `user-settings.json` to another machine won't transfer your activation.
- The `.aesgcm` bundle is encrypted with AES-256-GCM. Without your 24-word key, no one — not even the SQLTriage team — can read the bundle's contents.
- Your customer name binds cryptographically to the bundle via GCM Associated Data. Tampering with the bundle's metadata (or supplying a different customer name at activation time) causes decryption to fail closed. This is intentional: it watermarks each bundle so leaks are traceable.
- SQLTriage makes **no outbound network connections** during activation. No phone-home, no telemetry tied to your license, no online check.

## Support

Email support with any activation questions at the address provided in your activation email.

Include in your email:

- Your customer name (as you typed it in the activation field).
- The SQLTriage version + build number (Settings → About).
- The exact error message you see.
- Whether activation previously worked on this machine.

Most activation issues are typos in the customer name — copy/paste from your original activation email rather than retyping.
