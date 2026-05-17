#!/usr/bin/env bash
# Generér en deterministisk GUID fra PR-nummer + hemmelig seed.
# Brug: ./scripts/pr-guid.sh <pr-number>
#
# PREVIEW_SEED er en hex-streng (f.eks. 64 tegn) gemt som repo-secret.
# Samme PR-nummer + samme seed → samme GUID, hver gang.

set -euo pipefail

PR_NUMBER="${1:?Brug: pr-guid.sh <pr-number>}"
SEED="${PREVIEW_SEED:?Sæt PREVIEW_SEED som environment-variabel}"

# HMAC-SHA256, tag de første 32 hex-tegn og formatér som UUID v4-lignende
hash=$(echo -n "preview-${PR_NUMBER}" | openssl dgst -sha256 -hmac "$SEED" -hex | awk '{print $NF}')

# Formatér som 8-4-4-4-12 (ren kosmetik — gør URL'en genkendelig som en GUID)
echo "${hash:0:8}-${hash:8:4}-${hash:12:4}-${hash:16:4}-${hash:20:12}"
