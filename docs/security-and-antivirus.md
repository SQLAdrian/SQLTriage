---
layout: default
title: SQLTriage — Antivirus, SmartScreen & How to Verify the Download
description: Why an unsigned SQLTriage build may trip Windows SmartScreen or a generic antivirus heuristic, and exactly how to verify the download is safe — open source, SHA-256 provenance, VirusTotal, no telemetry.
---
<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Windows may warn you the first time — here's why, and how to verify

SQLTriage is currently distributed **unsigned** (a code-signing certificate is planned). Because of
that, Windows SmartScreen may show *"Windows protected your PC / unknown publisher"* on first run, and
an occasional antivirus may flag the download with a **generic, machine-learning** detection (names like
`Trojan.MSILZilla` or `Heur.*`). **This is a false positive** — and rather than ask you to take our word
for it, here is exactly why it happens and how you can prove the file is safe.

## Why it happens (the honest version)

- SQLTriage ships as a **single self-contained `.exe`** with the .NET runtime bundled and
  ahead-of-time compiled. To a generic heuristic, "a self-extracting executable that unpacks and loads
  compiled code" looks structurally similar to a packer — the same shape a great many legitimate .NET
  apps share. Signed binaries that have built download reputation sail through; a brand-new **unsigned**
  build has no reputation yet, so it gets the cautious treatment.
- It is **not** a signature match against known malware — it is a probabilistic guess by a model, and it
  does not reproduce reliably from build to build.

## How to verify it yourself

1. **The source is open.** Everything the community edition does is on
   [GitHub](https://github.com/SQLAdrian/SQLTriage) — read it, or build it yourself. There is no
   obfuscation.
2. **Verify the download hash.** Every release ships a `provenance-*.json` manifest listing the SHA-256
   of the artifact. Confirm your download matches:

   ```powershell
   Get-FileHash .\SQLTriage-*.zip -Algorithm SHA256
   ```

   The printed hash appears verbatim in that release's provenance manifest.
3. **Scan it independently.** Upload the exe to [VirusTotal](https://www.virustotal.com) — it is clean
   across the overwhelming majority of engines, with at most a stray generic heuristic.
4. **It cannot phone home.** SQLTriage is **agentless, read-only by default, and local-only** — no
   telemetry, no cloud, and no network calls except the SQL connections you configure. Watch it with a
   firewall if you like.

## To allow it

- **SmartScreen:** *More info → Run anyway*.
- **Antivirus:** if it is quarantined, restore it and add an exception, or submit it to your vendor as a
  false positive — vendors typically clear a generic FP within minutes of a report.

## What's next

We are acquiring a code-signing certificate. Once builds are signed, SmartScreen and most antivirus
engines will trust them by default and this notice becomes unnecessary. We would rather ship
transparently now — and tell you exactly what you are seeing and how to check it — than hide behind a
"just disable your antivirus" hand-wave. An audit tool should hold itself to the same standard it holds
your servers to.
