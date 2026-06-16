#!/usr/bin/env bash
# Recreate the perf worktrees + builds from a fresh clone of the Aaronontheweb fork.
# Run from inside the cloned akka.net repo root. Idempotent-ish (skips existing worktrees).
set -euo pipefail
ROOT="$(cd "$(git rev-parse --show-toplevel)" && pwd)"
PARENT="$(dirname "$ROOT")"
TFM=net10.0

echo "== prereqs =="
dotnet --version || { echo "Need the .NET 10 SDK (projects target $TFM)."; exit 1; }
for t in dotnet-trace dotnet-counters; do
  command -v $t >/dev/null || dotnet tool install -g $t || true
done
export PATH="$HOME/.dotnet/tools:$PATH"

git -C "$ROOT" fetch origin --prune

# config -> branch
declare -A WT=(
  [wt-dev]=dev                                      # A: DotNetty baseline
  [wt-decode]=experiment/remote-decode-fast-codec   # B (decode off) + C (decode on)
  [wt-stage0]=experiment/akka-io-output-pipe-stage0 # D: decode + Stage-0 output-pipe tuning (NEGATIVE result; re-confirm)
)
for w in "${!WT[@]}"; do
  p="$PARENT/$w"
  if [ -d "$p" ]; then echo "== $w exists, skipping worktree add =="; else
    echo "== worktree $w <- ${WT[$w]} =="
    git -C "$ROOT" worktree add "$p" "origin/${WT[$w]}" 2>/dev/null || git -C "$ROOT" worktree add "$p" "${WT[$w]}"
  fi
done

echo "== build RemotePingPong on each worktree ($TFM, Release) =="
for w in wt-dev wt-decode wt-stage0; do
  dotnet build "$PARENT/$w/src/benchmark/RemotePingPong/RemotePingPong.csproj" -c Release -f $TFM -p:WarningLevel=0
done
echo "== build Akka.Benchmarks on the decode worktree (isolated decode bench) =="
dotnet build "$PARENT/wt-decode/src/benchmark/Akka.Benchmarks/Akka.Benchmarks.csproj" -c Release -f $TFM -p:WarningLevel=0

echo "DONE. Worktrees + builds ready under $PARENT/{wt-dev,wt-decode,wt-stage0}."
echo "DLLs:"
for w in wt-dev wt-decode wt-stage0; do
  echo "  $w: $PARENT/$w/src/benchmark/RemotePingPong/bin/Release/$TFM/RemotePingPong.dll"
done
