#!/bin/bash
# Bygger cpn-preview-*.vsix klar til installation i VS Code.
# Kræver Node.js (https://nodejs.org).

set -e
cd "$(dirname "$0")"

if ! command -v npx &>/dev/null; then
    echo "Node.js / npx ikke fundet — installer fra https://nodejs.org"
    exit 1
fi

echo "Bygger VSIX …"
npx --yes @vscode/vsce package --no-dependencies

VSIX=$(ls -t cpn-preview-*.vsix | head -1)
echo ""
echo "✓  $VSIX"
echo ""
echo "Installer i VS Code:"
echo "   Extensions → ⋯ → Install from VSIX …  → vælg filen"
echo "eller:"
echo "   code --install-extension $VSIX"
