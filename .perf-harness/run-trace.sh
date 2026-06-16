#!/usr/bin/env bash
# Lock-contention trace: dotnet-trace each streams config + report the Monitor.Enter* share of
# non-idle work. This is the decisive test of the write-path lock (and whether Stage-0 / a future
# SPSC rewrite actually reduces it). Run on a QUIET box.
set -uo pipefail
PARENT="$(dirname "$(cd "$(git rev-parse --show-toplevel)" && pwd)")"
TFM=net10.0
export PATH="$HOME/.dotnet/tools:$PATH"
DEC="$PARENT/wt-decode/src/benchmark/RemotePingPong/bin/Release/$TFM/RemotePingPong.dll"
S0="$PARENT/wt-stage0/src/benchmark/RemotePingPong/bin/Release/$TFM/RemotePingPong.dll"
HERE="$(dirname "$0")"

echo "load: $(cat /proc/loadavg)"
echo "== trace C: streams+batch+decode =="
AKKA_FAST_DECODE=1 dotnet-trace collect --format speedscope -o /tmp/trace-decode.nettrace -- dotnet "$DEC" 1 stream >/dev/null 2>&1
echo "== trace D: streams+batch+decode+stage0 =="
AKKA_FAST_DECODE=1 dotnet-trace collect --format speedscope -o /tmp/trace-stage0.nettrace -- dotnet "$S0" 1 stream >/dev/null 2>&1
echo; echo "=== Monitor.Enter* (contended lock) share of non-idle work ==="
python3 "$HERE/parse_lockshare.py" /tmp/trace-decode.speedscope.json
python3 "$HERE/parse_lockshare.py" /tmp/trace-stage0.speedscope.json
echo
echo "Interpretation: if both are ~37% (Stage-0 doesn't help, as on the throttled box) => the lock is"
echo "producer/consumer flush/read SYNCHRONIZATION, not segment allocation => the fix is the lock-free"
echo "SPSC output hand-off (goal lever #1). Full method breakdown: python3 $HERE/parse_speedscope2.py /tmp/trace-decode.speedscope.json"
