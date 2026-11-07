# Built-in Serialization V2 native canary

Incremental proof for [#8718](https://github.com/akkadotnet/akka.net/issues/8718), run with
`Akka.DynamicTypeLoading=false` as an actual native executable. All actor systems use a local
provider; transport, cluster activation and generic reconstruction are separate claims.

| Protocol | Legacy / V2 ids | Executed fixture cases |
|---|---|---|
| Singleton | 14 / 54 | 12; identity, native buffer writes, nine expected rejections |
| Metrics | 10 / 50 | 24; eight required native writes, every numeric kind/value, timestamps, EWMA, router selectors and all five pins |
| PubSub | 9 / 49 | 51; seventeen required native writes, fifteen frozen legacy shapes including nulls, bytes and live actor-reference payloads |
| Cluster Client | 15 / 55 | 48; sixteen frozen reader pairs including nulls, nine custom payload envelopes and nine rejections |
| Actor-selection containers | 6 / 46 | 30; ten reader pairs including nulls, six actor-reference payloads, wildcard flag and seven rejections |
| Snapshot envelope | 8, same bytes | 15; five frozen legacy shapes across default/stripped/versioned old serializer names, required native writes, segmented reads, six user payloads and nine rejections |
| Persistence messages | 7, same bytes | 33; eleven fixed legacy shapes across three serializer-name spellings, required native writes, six manifest-alias reads, six user payloads and thirty-three rejections |

The two Persistence implementations retain their original ids, manifests and protobuf bytes;
the global V2 switch does not select them. The canary requires the native buffer writer for
both default registrations and for copied old public serializer names. Id 7 covers persistent
records, atomic writes, at-least-once delivery snapshots and fixed FSM state changes.
Generic `PersistentFSMSnapshot<T>` writes and reads must reject before caller output when
dynamic type loading is disabled; this is not native PersistentFSM support.

These consumers use public serialization APIs without a test assembly identity or blanket
assembly roots. PubSub's private domain types are obtained from frozen legacy records; their
fields are checked by re-encoding with the unchanged legacy writer. The client explicitly sets
its existing legacy-serialization setting off; bare stream refs and whole client delivery are
not claimed. Unknown paths must fail before the completion marker is printed.

This app is separate from the plain-core canary so adding a module does not change the
core-only boot and warning surface. It reuses `Shared/LogWatchdogFilter.cs`; runtime warnings
or errors fail the run. Later ports extend this corpus with actual round trips, including
generic payload reconstruction where applicable. Registering an id alone is not native proof.

## Run

Use the serviced SDK from `global.json` after the SDK update in #8914 (the local proof used
SDK 10.0.114 / runtime 10.0.14). Keep audit and warning checks enabled. Passing
`-p:PublishAot=true` globally would also change the library builds; the app enables it locally
when a runtime identifier is supplied.

```bash
set -o pipefail
rm -rf src/aot/Akka.Serialization.V2.AOT.App/bin src/aot/Akka.Serialization.V2.AOT.App/obj
dotnet publish src/aot/Akka.Serialization.V2.AOT.App -r linux-x64 -c Release \
  -o /tmp/v2-aot-publish 2>&1 | tee /tmp/v2-aot-publish.log
timeout 300 /tmp/v2-aot-publish/Akka.Serialization.V2.AOT.App
```

The run must exit 0 and print `[canary-v2] OK cases=12 metrics=24 pubsub=51 client=48 containers=30 snapshot=15 persistence=33 native-writes=4 rejected=9`.
The original 165 fixture cases remain required; Persistence adds 48 for a total of 213.
Each module also checks its own exact native-write and rejection counts before returning.
The runtime guard deliberately rejects managed execution; ordinary solution builds still
compile this app without native publishing.

## Warning and coverage boundaries

The cold-publish warning baseline covers Remote, Cluster, Persistence, Cluster.Tools, Cluster.Metrics and this app. Existing
legacy reflection warnings remain visible and are tracked individually; the Singleton port
adds none. The two Persistence entries are copied exactly from the existing Persistence canary
baseline after this cold publish observed them; they belong to the unchanged generic FSM
legacy reader/writer, not the new fixed-shape implementation. Core's separate rooted canary
owns the Akka.dll baseline. Dependencies without source
locations are counted separately by the checker and are not approved by this baseline.

```bash
dotnet run scripts/CheckAotWarnings.cs -- \
  --log /tmp/v2-aot-publish.log \
  --baseline src/aot/Akka.Serialization.V2.AOT.App/aot-warnings.baseline.txt \
  --scope "src/core/Akka.Remote/,src/core/Akka.Cluster/,src/core/Akka.Persistence/,src/contrib/cluster/Akka.Cluster.Tools/,src/contrib/cluster/Akka.Cluster.Metrics/,src/aot/Akka.Serialization.V2.AOT.App/" \
  --repo-root .
```

CI retains the full publish/run logs and checks both the exit status and completion marker.
Passing this corpus establishes these native codec paths, not Remote/Cluster startup,
network interoperability, singleton ownership hand-over, persistence journal/snapshot-store
activation or recovery, or arbitrary generic payload support.
