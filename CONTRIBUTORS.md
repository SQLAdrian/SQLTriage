<!-- In the name of God, the Merciful, the Compassionate -->
<!-- Bismillah ar-Rahman ar-Raheem -->

# Contributors

Thank you to all the contributors who have helped make this project better!

## Project Creator
- **Adrian** - [SQLAdrian](https://github.com/SQLAdrian)

## AI Development Assistants

This project acknowledges the significant assistance provided by modern AI coding tools during its development. Every AI-generated suggestion, code, or design has been thoroughly reviewed, tested, and validated by human engineers before inclusion.

| Assistant | Organization | Contribution Type | GitHub Profile |
|-----------|--------------|-------------------|----------------|
| **Claude Opus 4** | Anthropic | Architecture review, system design, code review, pre-mortem analysis, gap analysis | [anthropic](https://github.com/anthropic) |
| **Kilo** | Kilo AI | Implementation engineering, code generation, gap analysis, testing, documentation | [kilo-org](https://github.com/kilo-org) |
| **Cline** | cline.ai | Code implementation assistance, debugging, refactoring | [cline](https://github.com/cline) |
| **Amazon Q** | Amazon (AWS) | Code suggestions, documentation, AWS service guidance | [aws](https://github.com/aws) |
| **Grok** | xAI | Code generation and problem-solving assistance | [xai-org](https://github.com/xai-org) |

## How to Credit AI Contributions in Commits

When committing code that was produced with substantial AI assistance, use the `Co-Authored-By` trailer in the commit message to acknowledge the AI system that helped generate the changes:

```bash
git commit -m "feat: add GovernanceService capped scoring

Implement capped scoring algorithm with per-finding (40pt max),
per-category, and overall (100pt) caps. Add vector weights from
Config/governance-weights.json with hot-reload via IOptionsMonitor.

Co-Authored-By: Claude <noreply@anthropic.com>
Co-Authored-By: Kilo <noreply@kilo.ai>"
```

**GitHub Co-Authored-By syntax:**
```bash
Co-Authored-By: Name <email>
```

**Important:** Use a unique email per assistant to maintain separate attribution on GitHub:
- Claude: `noreply@anthropic.com`
- Kilo: `noreply@kilo.ai`
- Cline: `noreply@cline.ai`
- Amazon Q: `noreply@amazon.com` (or Q's designated email)
- Grok: `noreply@x.ai`

## Adding AI Assistants as Repository Collaborators (Optional)

To display these AI assistants in GitHub's Contributors graph, you can add them as external collaborators:

1. Go to **Settings → Collaborators** in your repository
2. Click **Add people**
3. Enter the GitHub username for each assistant (see table above)
4. Set role to **Read** (view access) — they cannot actually accept invitations
5. Add a note: "AI assistant contributor — no human access granted"

**Note:** External collaborators can only be added by repository owners and require a GitHub Enterprise plan for external collaborators on private repositories. The CONTRIBUTORS.md acknowledgment is sufficient for public recognition.

---

*Last updated: 2026-04-18 | SQLTriage v1.0.0-wip*