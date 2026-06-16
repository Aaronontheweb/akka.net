#!/usr/bin/env python3
"""Parse dotnet-trace EVENTED speedscope (multi-profile/thread): self & inclusive time per frame."""
import json, sys, collections

data = json.load(open(sys.argv[1]))
frames = data['shared']['frames']
def name(i): return frames[i]['name']

self_t = collections.defaultdict(float)
incl_t = collections.defaultdict(float)
grand = 0.0

for prof in data['profiles']:
    if prof.get('type') != 'evented':
        continue
    stack = []
    last_at = None
    for ev in prof['events']:
        at = ev['at']
        if last_at is not None and stack:
            dt = at - last_at
            if dt > 0:
                # dotnet-trace inserts synthetic CPU_TIME / UNMANAGED_CODE_TIME leaf markers;
                # the real leaf method is the frame just above them.
                leaf = stack[-1]
                ln = name(leaf)
                if ln in ('CPU_TIME', 'UNMANAGED_CODE_TIME', '?!?') and len(stack) >= 2:
                    leaf = stack[-2]
                self_t[leaf] += dt
                for f in set(stack):
                    incl_t[f] += dt
                grand += dt
        if ev['type'] == 'O':
            stack.append(ev['frame'])
        elif ev['type'] == 'C':
            # pop matching frame if present
            if stack and stack[-1] == ev['frame']:
                stack.pop()
            elif ev['frame'] in stack:
                # unwind to it
                while stack and stack[-1] != ev['frame']:
                    stack.pop()
                if stack: stack.pop()
        last_at = at

cats = {
    'GC/alloc':       ['GCHeap','gc_heap','GarbageCollect','AllocateObject','JIT_New','WKS::','SVR::','gc_','Gen0','set_','.ctor'],
    'decode/parse':   ['Decode','Pdu','Protobuf','CodedInput','MergeFrom','AkkaPdu','ParseFrom','Parser'],
    'serialize':      ['Serializ','ToBinary','FromBinary','WriteTo','CodedOutput','MessageSerializer','ToProto','Deserialize'],
    'framing/stream': ['Framing','TcpStream','GraphStage','GraphInterpreter','ReadOnlySequence','SequenceSegment','RemoteTcpFram','Streams'],
    'actor/mailbox':  ['Mailbox','Dispatch','Invoke','SendMessage','ProcessMessage','ActorCell','RunMessage','ActorRef','Envelope','Tell'],
    'socket/io':      ['Socket','epoll','SocketAsync','Poll','IOCP'],
    'threadpool':     ['ThreadPool','WorkItem','Queue','Worker'],
}
cat_self = collections.defaultdict(float)
for fidx, w in self_t.items():
    n = name(fidx); placed=False
    for cat, keys in cats.items():
        if any(k in n for k in keys):
            cat_self[cat]+=w; placed=True; break
    if not placed: cat_self['other']+=w

print(f"=== total on-CPU time: {grand:.0f} ms across {len(data['profiles'])} threads ===\n")
print("--- TOP 35 by SELF (on-CPU leaf) time ---")
for fidx,w in sorted(self_t.items(),key=lambda x:-x[1])[:35]:
    print(f"{w/grand*100:6.2f}%  {name(fidx)[:100]}")
print("\n--- category breakdown (self time) ---")
for cat,w in sorted(cat_self.items(),key=lambda x:-x[1]):
    print(f"{w/grand*100:6.2f}%  {cat}")
print("\n--- TOP 25 by INCLUSIVE time ---")
for fidx,w in sorted(incl_t.items(),key=lambda x:-x[1])[:25]:
    print(f"{w/grand*100:6.2f}%  {name(fidx)[:100]}")
