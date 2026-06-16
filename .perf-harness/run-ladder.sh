#!/usr/bin/env bash
# Aggregate ladder: throughput (peak msgs/sec) + allocation (bytes/msg via counters) for
# A=dev/DotNetty, B=streams+batch(decode off), C=+decode, D=+decode+stage0.
# Run on a QUIET box (DotNetty thread-thrashes under load and reads artificially low).
# Usage: ./run-ladder.sh [REPS]   (default 5)
set -uo pipefail
REPS="${1:-5}"
PARENT="$(dirname "$(cd "$(git rev-parse --show-toplevel)" && pwd)")"
TFM=net10.0
export PATH="$HOME/.dotnet/tools:$PATH"
DEV="$PARENT/wt-dev/src/benchmark/RemotePingPong/bin/Release/$TFM/RemotePingPong.dll"
DEC="$PARENT/wt-decode/src/benchmark/RemotePingPong/bin/Release/$TFM/RemotePingPong.dll"
S0="$PARENT/wt-stage0/src/benchmark/RemotePingPong/bin/Release/$TFM/RemotePingPong.dll"
maxtp(){ grep -E "^\s+[0-9]+," | awk -F, '{gsub(/ /,"",$3); if($3+0>m)m=$3} END{print m+0}'; }

echo "load@start: $(cat /proc/loadavg)"
echo "=== THROUGHPUT (peak msgs/sec, interleaved) ==="
for r in $(seq 1 "$REPS"); do
  a=$(dotnet "$DEV" 1 2>/dev/null | maxtp)
  b=$(AKKA_FAST_DECODE=0 dotnet "$DEC" 1 stream 2>/dev/null | maxtp)
  c=$(AKKA_FAST_DECODE=1 dotnet "$DEC" 1 stream 2>/dev/null | maxtp)
  d=$(AKKA_FAST_DECODE=1 dotnet "$S0"  1 stream 2>/dev/null | maxtp)
  echo "rep $r | A=$a | B=$b | C=$c | D=$d"
done

echo; echo "=== ALLOCATION (bytes/msg via dotnet-counters, interleaved) ==="
rm -f /tmp/lad_*.csv
for r in $(seq 1 3); do
  dotnet-counters collect --refresh-interval 1 --format csv -o /tmp/lad_A_$r.csv -- dotnet "$DEV" 1 >/dev/null 2>&1
  AKKA_FAST_DECODE=0 dotnet-counters collect --refresh-interval 1 --format csv -o /tmp/lad_B_$r.csv -- dotnet "$DEC" 1 stream >/dev/null 2>&1
  AKKA_FAST_DECODE=1 dotnet-counters collect --refresh-interval 1 --format csv -o /tmp/lad_C_$r.csv -- dotnet "$DEC" 1 stream >/dev/null 2>&1
  AKKA_FAST_DECODE=1 dotnet-counters collect --refresh-interval 1 --format csv -o /tmp/lad_D_$r.csv -- dotnet "$S0"  1 stream >/dev/null 2>&1
  echo "counters rep $r done"
done
python3 "$(dirname "$0")/parse_ladder.py" A B C D
echo "load@end: $(cat /proc/loadavg)"
