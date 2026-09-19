#!/usr/bin/env bash
# Downloads the public instrument masters the broker reads at start:
#   - FYERS symbol masters: symbols, lot sizes, tick sizes, trading sessions
#   - Dhan's detailed scrip master: exchange freeze quantities
# into data/instruments/ (or the folder given as the first argument).
set -euo pipefail

target="${1:-$(cd "$(dirname "$0")/.." && pwd)/data/instruments}"
mkdir -p "$target"

fetch() {
  local url="$1" file="$2"
  echo "-> $file"
  curl --fail --silent --show-error --location --retry 3 --output "$target/$file.part" "$url"
  mv "$target/$file.part" "$target/$file"
}

for master in NSE_CM NSE_FO BSE_CM BSE_FO MCX_COM; do
  fetch "https://public.fyers.in/sym_details/$master.csv" "$master.csv"
done
fetch "https://images.dhan.co/api-data/api-scrip-master-detailed.csv" "api-scrip-master-detailed.csv"

echo "Instrument masters saved in $target"
