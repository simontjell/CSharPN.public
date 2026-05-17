#!/bin/bash
###############################################################################
# build-and-serve-wasm.sh
# 
# Bygger og hoster CSharPN.Wasm (Blazor WebAssembly app)
#
# Brug:
#   ./scripts/build-and-serve-wasm.sh [options]
#
# Optioner:
#   --host <host>       Server vært (default: localhost)
#   --port <port>       Server port (default: 8080)
#   --no-build          Skip build step, kun host
#   --no-open           Åbn ikke browser automatisk
#   --help              Vis denne hjælp
###############################################################################

set -e

# Standard værdier
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WASM_CSPROJ="$PROJECT_ROOT/src/CSharPN.Wasm/CSharPN.Wasm.csproj"
PUBLISH_DIR="$PROJECT_ROOT/publish/wasm"
WWWROOT_DIR="$PUBLISH_DIR/wwwroot"

HOST="localhost"
PORT="8080"
DO_BUILD=true
DO_OPEN=true

# Parse argumenter
while [[ $# -gt 0 ]]; do
    case $1 in
        --host)
            HOST="$2"
            shift 2
            ;;
        --port)
            PORT="$2"
            shift 2
            ;;
        --no-build)
            DO_BUILD=false
            shift
            ;;
        --no-open)
            DO_OPEN=false
            shift
            ;;
        --help)
            grep "^#" "$0" | sed 's/^# *//' | sed 's/^##*//'
            exit 0
            ;;
        *)
            echo "Ukendt option: $1" >&2
            exit 1
            ;;
    esac
done

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "CSharPN WebAssembly Builder & Server"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"

# Build
if [ "$DO_BUILD" = true ]; then
    echo ""
    echo "📦 Build af CSharPN.Wasm (Release)..."
    echo ""
    
    dotnet publish "$WASM_CSPROJ" \
        --configuration Release \
        --output "$PUBLISH_DIR" \
        --no-restore \
        --verbosity normal
    
    echo ""
    echo "✅ Build gennemført!"
fi

# Tjek at wwwroot eksisterer
if [ ! -d "$WWWROOT_DIR" ]; then
    echo "" >&2
    echo "❌ Fejl: wwwroot-mappe ikke fundet på $WWWROOT_DIR" >&2
    echo "   Kør først med --build eller dotnet publish manuelt." >&2
    exit 1
fi

# Start server
echo ""
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "🌐 Starter server på http://$HOST:$PORT"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "📂 Folder: $WWWROOT_DIR"
echo ""
echo "Tryk Ctrl+C for at stoppe serveren"
echo ""

# Prøv at åbne browser
if [ "$DO_OPEN" = true ]; then
    URL="http://$HOST:$PORT"
    if command -v "$BROWSER" &> /dev/null; then
        "$BROWSER" "$URL" &
    elif command -v xdg-open &> /dev/null; then
        xdg-open "$URL" &
    elif command -v open &> /dev/null; then
        open "$URL" &
    fi
fi

# Start Python HTTP server
cd "$WWWROOT_DIR"
python3 -m http.server "$PORT" --bind "$HOST"
