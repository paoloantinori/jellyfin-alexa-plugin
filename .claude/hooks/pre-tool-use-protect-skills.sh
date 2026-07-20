#!/usr/bin/env bash
# BLOCKS any SMAPI delete-skill command.
# Alexa skill deletion is IRREVERSIBLE — skills must NEVER be deleted
# without the user's explicit approval in conversation.
#
# This hook intercepts Bash tool calls containing "delete-skill" SMAPI commands.

# Read the tool input from stdin (JSON with "command" field)
INPUT=$(cat)

# Extract the command from the JSON input
CMD=$(echo "$INPUT" | python3 -c "import sys,json; print(json.load(sys.stdin).get('command',''))" 2>/dev/null)

# Block any SMAPI delete-skill command
if echo "$CMD" | grep -qi "delete-skill"; then
    echo "BLOCKED: SMAPI delete-skill is permanently blocked by project hook."
    echo "Skill deletion is IRREVERSIBLE. Ask the user for explicit permission first."
    echo "If approved, the user can run the command manually in their terminal."
    exit 2  # non-zero exit blocks the tool call
fi

# Allow all other commands
exit 0
