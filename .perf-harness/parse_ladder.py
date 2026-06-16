#!/usr/bin/env python3
"""bytes/msg + GC pause per config from dotnet-counters CSVs named lad_<CFG>_<rep>.csv.
bytes/msg = total bytes allocated / 21.2M msgs-per-run (identical RPP phases across transports) =>
LOAD-INVARIANT. Pass config letters as argv (default A B C D)."""
import csv, glob, statistics, sys
TOTAL_MSGS = 200000+1000000+2000000+3000000+4000000+5000000+6000000  # 21.2M
LABELS = {'A':'dev / DotNetty', 'B':'streams+batch (decode off)', 'C':'streams+batch+decode',
          'D':'streams+batch+decode+stage0'}
def run(csvp):
    rows=list(csv.DictReader(open(csvp)))
    alloc=[float(r['Mean/Increment']) for r in rows if 'gc.heap.total_allocated' in r['Counter Name']]
    pause=[float(r['Mean/Increment']) for r in rows if 'gc.pause.time' in r['Counter Name']]
    active=[v for v in alloc if v>5e8]
    gcp=statistics.mean([p for p,a in zip(pause,alloc) if a>5e8]) if active else 0
    return sum(alloc)/TOTAL_MSGS, gcp
cfgs=sys.argv[1:] or ['A','B','C','D']
res={}
for k in cfgs:
    bpm=[];gc=[]
    for f in sorted(glob.glob(f'/tmp/lad_{k}_*.csv')):
        b,g=run(f); bpm.append(b); gc.append(g)
    if bpm:
        res[k]=statistics.median(bpm)
        print(f"{k} {LABELS.get(k,k):32s}: bytes/msg median {statistics.median(bpm):,.0f}  GCpause {statistics.median(gc)*100:.1f}%  runs {[f'{x:,.0f}' for x in bpm]}")
def d(x,y,lbl):
    if x in res and y in res and res[x]: print(f"  {lbl}: {res[y]/res[x]-1:+.1%}")
print("\n-- deltas (allocation/msg) --")
d('A','B','B vs A (streams effort)'); d('B','C','C vs B (decode codec)')
d('A','C','C vs A (full stack vs DotNetty)'); d('C','D','D vs C (stage0 lock tuning)')
