#!/usr/bin/env python3
# Compute the contended-lock (Monitor.Enter*) share of NON-IDLE work from a dotnet-trace speedscope.
import json, sys, collections
data=json.load(open(sys.argv[1]))
frames=data['shared']['frames']
def name(i): return frames[i]['name']
self_t=collections.defaultdict(float)
for prof in data['profiles']:
    if prof.get('type')!='evented': continue
    stack=[]; last=None
    for ev in prof['events']:
        at=ev['at']
        if last is not None and stack:
            dt=at-last
            if dt>0:
                leaf=stack[-1]; ln=name(leaf)
                if ln in ('CPU_TIME','UNMANAGED_CODE_TIME','?!?') and len(stack)>=2: leaf=stack[-2]
                self_t[leaf]+=dt
        if ev['type']=='O': stack.append(ev['frame'])
        elif ev['type']=='C':
            if stack and stack[-1]==ev['frame']: stack.pop()
            elif ev['frame'] in stack:
                while stack and stack[-1]!=ev['frame']: stack.pop()
                if stack: stack.pop()
        last=at
IDLE=['Semaphore','WaitHandle','WaitOne','Monitor.Wait','SpinWait','SpinOnce','SpinWaiter','WorkerThreadStart',
      'GateThread','PollGC','?!?','RunFinalizers','DestroyScout','WaitNative','Park','Sleep','EventLoop',
      'WaitForSignal','Dispatch()','StartCallback','Finalize','ThreadStart','Release(']
def isidle(n): return any(k in n for k in IDLE)
work={f:w for f,w in self_t.items() if not isidle(name(f))}
tot=sum(work.values())
lock=sum(w for f,w in work.items() if 'Monitor.Enter' in name(f) or 'Monitor.Exit' in name(f))
enter_slow=sum(w for f,w in work.items() if 'Monitor.Enter_Slowpath' in name(f))
print(f"{sys.argv[1].split('/')[-1]}: work={tot:.0f}ms  Monitor.Enter*+Exit*={lock/tot*100:.1f}%  (Enter_Slowpath alone {enter_slow/tot*100:.1f}%)")
