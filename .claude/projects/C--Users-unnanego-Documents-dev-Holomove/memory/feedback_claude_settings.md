---
name: Keep .claude/ tracked in git
description: User wants .claude/ settings committed to git, not gitignored — they work on the project from multiple computers
type: feedback
---

Do NOT add .claude/ to .gitignore or remove it from git tracking.

**Why:** User is the sole developer and works from different computers, so they want Claude Code settings to persist across machines via git.

**How to apply:** When updating .gitignore or cleaning up tracked files, leave .claude/ alone.
