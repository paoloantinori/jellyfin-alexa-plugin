#!/usr/bin/env bash
# BLOCKS any SMAPI delete-skill command.
# Alexa skill deletion is IRREVERSIBLE — skills must NEVER be deleted
# without the user's explicit approval in conversation.
#
# This hook intercepts Bash tool calls containing "delete-skill" SMAPI commands.

# Read the tool input from stdin (JSON with the command under tool_input)
INPUT=$(cat)

# Extract the command from the JSON input (tool_input.command is the documented
# PreToolUse field; the top-level key is kept as a fallback for direct invocations)
CMD=$(echo "$INPUT" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d.get('tool_input',{}).get('command') or d.get('command',''))" 2>/dev/null)

# Block any SMAPI delete-skill command
if echo "$CMD" | grep -qi "delete-skill"; then
    # stderr, not stdout: on exit 2 the harness feeds stderr back to the model
    # as the denial reason (stdout is not shown to it)
    echo "BLOCKED: SMAPI delete-skill is permanently blocked by project hook." >&2
    echo "Skill deletion is IRREVERSIBLE. Ask the user for explicit permission first." >&2
    echo "If approved, the user can run the command manually in their terminal." >&2
    exit 2  # exit 2 blocks the tool call
fi

# Allow all other commands
exit 0
