# Akka.Remote streams-transport perf — reproduction harness (bare-metal re-baseline)

This recreates and re-runs the whole Phase-2 perf experiment from a clean clone of the
**Aaronontheweb fork**, so a fast unthrottled box (Ryzen) can produce trustworthy numbers. Every
absolute number from the original work was measured on a **throttled box (load 2–12 on 8 cores)** — the
*allocation* deltas are load-invariant and held up, but **throughput was unreliable** (DotNetty
thread-thrashes under load). Re-baseline here.

Memorizer context (project `676404da-137a-4f07-9525-22bc78e5ac33`): goal `fd80aa7d` (has the up-to-date
status + remaining experiments + current `/goal` prompt), living ledger `b5951bd2`, record chain
`50502a49`→`77943b4c`→`cb86c749`→`8838e175`, aggregate `e7c9e887`, lock design `bb53c392`.

---

## The branches under test (all on `origin` = https://github.com/Aaronontheweb/akka.net)

| cfg | branch | commit | what it is | how RPP is invoked |
|----|--------|--------|------------|--------------------|
| **A** | `dev` | `c6e1dfc13` | shipping baseline, **DotNetty** transport | `RemotePingPong 1` |
| **B** | `experiment/remote-decode-fast-codec` | `bcf5ffcac` | streams transport (#8240) + inbound batching (#8270), **decode OFF** | `AKKA_FAST_DECODE=0 RemotePingPong 1 stream` |
| **C** | `experiment/remote-decode-fast-codec` | `bcf5ffcac` | …+ **decode codec** (B0+B2a+B2b) | `AKKA_FAST_DECODE=1 RemotePingPong 1 stream` |
| **D** | `experiment/akka-io-output-pipe-stage0` | `5f6b18a7d` | …+ **Stage-0** output-pipe tuning (a **NEGATIVE** result — re-confirm) | `AKKA_FAST_DECODE=1 RemotePingPong 1 stream` |

`AKKA_FAST_DECODE` is an experiment-only A/B toggle in `EndpointReader.TryDecodeMessageAndAck` (default =
fast). `0` forces the original generated-protobuf decode (oracle); `1` uses the hand-rolled
`DecodeMessageFast`. B0 is present in both arms (allocation-neutral). Underlying bases: #8240
`feature/remote-streams-protocol-pipeline`, #8269 `perf/streams-tcp-write-windowing`, #8270
`experiment/remote-inbound-pdu-batching` are all on the fork too (folded into B/C/D history).

---

## Prereqs
- **.NET 10 SDK** (projects target `net10.0`; `global.json` pins an SDK but `net10.0` requires the .NET 10
  toolchain — install it, or set `rollForward`).
- `git`, `python3`, and dotnet global tools `dotnet-trace` + `dotnet-counters`
  (`dotnet tool install -g dotnet-trace dotnet-counters`; ensure `~/.dotnet/tools` on `PATH`).
- A **quiet** machine for throughput/trace runs. Server GC is already configured by the benchmark.

## 1. Clone + recreate worktrees + build
```bash
git clone https://github.com/Aaronontheweb/akka.net.git akka.net && cd akka.net
git checkout perf/measurement-harness        # this harness lives here
.perf-harness/setup.sh                        # creates ../wt-dev, ../wt-decode, ../wt-stage0 + builds RPP + Akka.Benchmarks
```

## 2. Correctness gates (re-confirm on the new box BEFORE trusting perf)
```bash
TFM=net10.0
# decode differential (the wire-interop safety net) — expect 8/8
dotnet test ../wt-decode/src/core/Akka.Remote.Tests/Akka.Remote.Tests.csproj -c Release -f $TFM \
  --filter "FullyQualifiedName~AkkaPduCodecFastDecodeDifferentialSpec"
# remote message-path suite with fast decode live — expect 89/89
dotnet test ../wt-decode/src/core/Akka.Remote.Tests/Akka.Remote.Tests.csproj -c Release -f $TFM \
  --filter "FullyQualifiedName~RemotingSpec|FullyQualifiedName~EndpointReaderSpec|FullyQualifiedName~AkkaPduCodec|FullyQualifiedName~AkkaProtocolSpec|FullyQualifiedName~Delivery|FullyQualifiedName~MessageDispatcher|FullyQualifiedName~RemoteMessageSerialization|FullyQualifiedName~RemoteDeploy"
# Akka.IO TCP gates for Stage-0 (write path) — expect 24/24 and 21/21(+3 skip)
dotnet test ../wt-stage0/src/core/Akka.Tests/Akka.Tests.csproj -c Release -f $TFM \
  --filter "FullyQualifiedName~Akka.Tests.IO.TcpConnectionSpec|FullyQualifiedName~Akka.Tests.IO.TcpIntegrationSpec"
dotnet test ../wt-stage0/src/core/Akka.Streams.Tests/Akka.Streams.Tests.csproj -c Release -f $TFM \
  --filter "FullyQualifiedName~Akka.Streams.Tests.IO.TcpSpec"
```

## 3. Isolated decode benchmark (cleanest, lowest-noise decode number)
```bash
dotnet run -c Release --project ../wt-decode/src/benchmark/Akka.Benchmarks/Akka.Benchmarks.csproj -f $TFM -- \
  --filter "*AkkaPduCodecBenchmark.DecodeMessageOnlyFromSequence" "*AkkaPduCodecBenchmark.DecodeMessageOnlyFast"
```
Throttled-box reference: oracle **669 ns / 1048 B** vs fast **406 ns / 416 B** = **−39% CPU, −60% alloc**.
Bare metal will be faster in absolute ns; the **ratio** should hold.

## 4. Aggregate ladder — throughput + allocation (THE re-baseline)
```bash
.perf-harness/run-ladder.sh 5      # 5 interleaved throughput reps + 3 counters reps; prints bytes/msg + deltas
```
- **Throughput** (peak msgs/sec): the number that was unreliable under load. On a quiet box expect a clean
  A≪B<C ladder. Throttled rough single-runs were: A(DotNetty)~568k, B(streams+batch)~820–850k, C(+decode)~875k.
- **Allocation** (bytes/msg, load-invariant): throttled-box result was A **4,949** → B **3,581** → C **3,103**
  ⇒ **−37.3%** C-vs-A, GC pause **20.0%→13.5%**. D (Stage-0) ≈ C (no change).

## 5. Lock trace — the write-path `Pipe._sync` contention (decides the next big lever)
```bash
.perf-harness/run-trace.sh         # traces C and D, prints Monitor.Enter* share of non-idle work
```
Throttled-box result: **C = 37.9%**, **D = 37.5%** ⇒ **Stage-0 does NOT reduce the lock**. If bare metal
agrees, the contention is producer↔consumer flush/read **synchronization** on `Pipe._sync` (not segment
allocation), and the only fix is the **lock-free SPSC output hand-off** (goal lever #1 — the big/risky
Akka.IO rewrite). If bare metal *disagrees* (Stage-0 helps), revisit cheap PipeOptions tuning first.

---

## What to expect / decision tree
1. **Allocation** should reproduce closely (load-invariant): full stack **~−37%** bytes/msg vs DotNetty.
2. **Throughput** is the unknown the throttling hid. A clean quiet-box ladder tells us how big the streams
   effort (B vs A) and the decode codec (C vs B) really are. Decode is CPU-neutral-ish on allocation but
   ~−39% decode CPU in isolation; whether that surfaces as RPP throughput depends on how GC/lock-bound RPP is.
3. **Lock**: if Stage-0 still doesn't move `Monitor.Enter*` (~37%), commit to the SPSC rewrite. The lock is
   now the **#1** cost (it grew from 27%→37% *because* the decode work shrank).

## Next experiment to build (per goal `fd80aa7d`)
**Lock-free SPSC output hand-off** in `Akka/IO/TcpTransportConnection.cs`: replace the output `Pipe`
(producer = actor dispatcher, consumer = write-pump) with a lock-free single-producer/single-consumer
buffer queue; wake the consumer via `ManualResetValueTaskSource` (NOT `Channel` — it locks). Reimplement
backpressure (pause/resume), completion/flush-on-complete, write-failure-fails-pending-IN-ORDER, abort,
cancellation. Gate with the §2 Akka.IO TCP suite, then re-run §4/§5 here. Mirror for the input pipe (read
path ~30% of the lock). Extend-only; no public API/behavior change.
