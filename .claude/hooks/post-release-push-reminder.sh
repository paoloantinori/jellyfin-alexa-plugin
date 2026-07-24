#!/usr/bin/env bash
# PostToolUse hook (Bash): after a release-tag push, remind to set curated GitHub
# release notes. release-build.yml uses generate_release_notes, which only lists PR
# titles — bare (a ~100-byte compare link) for this direct-to-main repo. Recurring
# miss. See CLAUDE.md Release checklist step 5 + memory: github-release-notes.
#
# Always exits 0 (a PostToolUse reminder must never block the tool).

input="$(cat)"
cmd="$(printf '%s' "$input" | jq -r '.tool_input.command // ""' 2>/dev/null)"

# Match a release-tag push: "git push" AND (--tags OR an x.y.z[.w] version).
if printf '%s' "$cmd" | grep -qE 'git[[:space:]]+push' \
   && printf '%s' "$cmd" | grep -qE -- '--tags|[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?'; then
  cat <<'EOF'
⚠️ Release tag push detected — MANDATORY follow-up (do not end the turn without doing this):
after release-build.yml CI succeeds, set CURATED GitHub release notes (the auto body is a
bare ~100-byte compare link for this direct-to-main repo):
    gh release edit <tag> --notes-file /tmp/release_notes_<tag>.md
    gh release view <tag> --json body -q '.body' | wc -c   # must be hundreds+ bytes, not ~100
(CLAUDE.md Release checklist step 5; memory: github-release-notes)
EOF
fi

exit 0
