#!/bin/bash
# Installs the toolchain a Claude Code on the web session needs to build Nocturne and run its
# tests. The container image does not ship a .NET SDK, so without this every session spends its
# first minutes downloading one before it can compile anything.
#
# Local sessions are left alone: a developer's machine has its own SDK and its own PATH.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

# global.json pins the SDK band, so read the channel from it rather than restating it here — a
# repo that moves to .NET 11 must not leave this hook installing 10.
CHANNEL="$(grep -oP '"version"\s*:\s*"\K[0-9]+\.[0-9]+' "${CLAUDE_PROJECT_DIR:-.}/global.json" 2>/dev/null || echo "10.0")"

if command -v dotnet >/dev/null 2>&1; then
  echo "dotnet already on PATH: $(dotnet --version)"
elif [ -x "$DOTNET_ROOT/dotnet" ]; then
  echo "dotnet already installed at $DOTNET_ROOT"
else
  echo "Installing .NET SDK $CHANNEL into $DOTNET_ROOT"
  curl -sSL --retry 3 --max-time 300 https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --channel "$CHANNEL" --install-dir "$DOTNET_ROOT"
  rm -f /tmp/dotnet-install.sh
fi

export PATH="$DOTNET_ROOT:$PATH"

# Persisted for the session: without this the agent has to prefix every command with the path.
{
  echo "export DOTNET_ROOT=\"$DOTNET_ROOT\""
  echo "export PATH=\"$DOTNET_ROOT:\$PATH\""
  # The first build otherwise spends a minute printing a welcome banner and seeding a CLI profile.
  echo 'export DOTNET_NOLOGO=1'
  echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1'
} >> "${CLAUDE_ENV_FILE:-/dev/null}"

dotnet --version

# Warms the NuGet cache so the first real build is not also the first restore. The Windows-only
# desktop and widget projects cannot restore on Linux, so restore the solution but do not fail the
# session when they are the only thing that failed — everything the agent builds lives elsewhere.
cd "${CLAUDE_PROJECT_DIR:-.}"
dotnet restore nocturne.sln --nologo >/tmp/nocturne-restore.log 2>&1 \
  || echo "Restore reported errors (expected for the Windows-only projects); see /tmp/nocturne-restore.log"

echo "Ready: dotnet $(dotnet --version)"
