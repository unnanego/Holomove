---
name: Repo cleanup before going public
description: Credentials in git history need scrubbing before making the repo public
type: project
---

Password `cezmx6#DBjJrjL#rOy` and JWT tokens were committed to git history in early commits. Repo is currently private.

**Why:** User plans to make the repo public eventually.

**How to apply:** Before making the repo public, need to rewrite git history (e.g. `git filter-repo`) to scrub credentials, or start fresh with a squashed initial commit.
