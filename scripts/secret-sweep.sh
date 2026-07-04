#!/usr/bin/env bash
# Security sweep (guide §4.3, §8, acceptance item 15): fail if an Anthropic key
# (sk-ant-…) appears anywhere in the repo. Run in CI and locally before commits.
#
# Also flags the common mistake of pasting the key into a Unity config asset.
set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"

echo "== Quest Claude secret sweep =="

fail=0

# 1. Anthropic API keys anywhere (excluding git internals and this script).
if grep -rInE 'sk-ant-[a-zA-Z0-9_-]+' \
      --exclude-dir=.git \
      --exclude-dir=node_modules \
      --exclude="secret-sweep.sh" \
      . ; then
  echo "ERROR: found something that looks like an Anthropic API key (sk-ant-…)."
  fail=1
else
  echo "OK: no sk-ant- keys found."
fi

# 2. Committed secret files that should be git-ignored.
for f in proxy/.dev.vars proxy/.env .env ; do
  if git ls-files --error-unmatch "$f" >/dev/null 2>&1 ; then
    echo "ERROR: $f is tracked by git — it must be git-ignored."
    fail=1
  fi
done
echo "OK: no secret env files are tracked."

# 3. Warn if the Anthropic key was pasted into the Unity config asset.
if [ -f unity-app/Assets/QuestClaude/AssistantConfig.asset ] \
   && grep -qE 'sk-ant-' unity-app/Assets/QuestClaude/AssistantConfig.asset 2>/dev/null ; then
  echo "ERROR: Anthropic key found in AssistantConfig.asset — only the app token belongs there."
  fail=1
fi

if [ "$fail" -ne 0 ]; then
  echo "== SWEEP FAILED =="
  exit 1
fi
echo "== SWEEP PASSED =="
