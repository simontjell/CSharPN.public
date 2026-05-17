#!/bin/bash
###############################################################################
# serve.sh
#
# Starter CSharPN.Visualizer.Server (Blazor Server) til lokal udvikling.
# Giver adgang til alle features inkl. C#-editoren (Roslyn kører server-side).
#
# Brug:
#   ./scripts/serve.sh [--port <port>] [<model.cs>]
#
# Optioner:
#   --port <port>   Port (default: 5000)
#   <model.cs>      Valgfri: vis kun denne model (ingen dropdown i UI)
#   --help          Vis denne hjælp
###############################################################################

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SERVER_CSPROJ="$PROJECT_ROOT/src/CSharPN.Visualizer.Server/CSharPN.Visualizer.Server.csproj"

PORT="5000"
MODEL_FILE=""

while [[ $# -gt 0 ]]; do
    case $1 in
        --port) PORT="$2"; shift 2 ;;
        --help)
            grep "^#" "$0" | sed 's/^# *//' | sed 's/^##*//'
            exit 0
            ;;
        *.cs)
            MODEL_FILE="$(realpath "$1")"
            if [[ ! -f "$MODEL_FILE" ]]; then
                echo "Fejl: filen '$1' findes ikke." >&2
                exit 1
            fi
            shift
            ;;
        *) echo "Ukendt option: $1" >&2; exit 1 ;;
    esac
done

echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo "CSharPN Visualizer Server"
echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
echo ""
echo "  http://localhost:$PORT"
if [[ -n "$MODEL_FILE" ]]; then
echo "  Model:      $MODEL_FILE"
echo "  Hot reload: gem filen for at genindlaese modellen"
fi
echo ""
echo "Tryk Ctrl+C for at stoppe"
echo ""

# Kill any existing process on the port
kill $(lsof -ti :"$PORT") 2>/dev/null || true
sleep 0.3

export ASPNETCORE_URLS="http://0.0.0.0:$PORT"
export ASPNETCORE_ENVIRONMENT="Development"
export CSHARPN_MODEL_FILE="$MODEL_FILE"
exec dotnet run --project "$SERVER_CSPROJ" --no-launch-profile
