"""Seed exact baseline inventory and retain native generation dispositions.

Reallocation successors are separate from free events. A generation retired
by a native free may have a live successor; neither proves owner cleanup.
"""
from collections import namedtuple
from heap_trace_model import Replay, Block
Death=namedtuple('Death','qpc reason address size')
Birth=namedtuple('Birth','qpc address size tid seeded')

class CohortReplay:
    def __init__(self,heap):
        self.heap=heap;self.model=Replay();self.seeded=False;self.births={};self.deaths={};self.successors={};self.baseline={}
    @staticmethod
    def require(ok,message):
        if not ok:raise ValueError(message)
    def current(self):return {address:b for (heap,address),b in self.model.live.items() if heap==self.heap}
    def seed(self,busy,qpc):
        self.require(not self.seeded,'baseline seeded twice')
        live=self.current();self.require(all(a in busy and b.size==busy[a] for a,b in live.items()),'traced initial block conflicts with baseline')
        for address,size in busy.items():
            if address not in live:
                self.model.generation+=1;b=Block(size,None,self.model.generation,0,qpc,0)
                self.model.live[self.heap,address]=b;self.births[b.generation]=Birth(qpc,address,size,0,True)
        self.seeded=True;self.baseline=self.current();self.validate(busy)
    def validate(self,busy):
        actual={address:b.size for address,b in self.current().items()}
        missing=sorted(set(busy)-set(actual));extra=sorted(set(actual)-set(busy));wrong=sorted(a for a in actual.keys()&busy.keys() if actual[a]!=busy[a])
        self.require(not missing and not extra and not wrong,'complete native snapshot mismatch: '+str(dict(Missing=len(missing),Extra=len(extra),WrongSize=len(wrong),MissingExamples=missing[:8],ExtraExamples=extra[:8],WrongSizeExamples=wrong[:8])))
    def destroy(self,heap,qpc):
        if heap==self.heap and self.seeded:
            for address,b in self.current().items():self.deaths[b.generation]=Death(qpc,'HeapDestroy',address,b.size)
        self.model.destroy(heap)
    def apply(self,op,tid,qpc,fields):
        heap,address=fields[:2]
        if heap!=self.heap:
            self.model.apply(op,tid,qpc,fields);return
        key=(heap,address);oldkey=(heap,fields[2]) if op==34 else key
        before={k:self.model.live.get(k) for k in {key,oldkey}}
        oldfree=self.model.freed.get(oldkey)
        if self.seeded and op==36:self.require(before[key] is not None,'post-baseline free has no live generation')
        if self.seeded and op==34 and before[oldkey] is None:
            self.require(key!=oldkey and before[key] is not None and oldfree is not None and oldfree.generation is not None,'post-baseline reallocation lacks original generation')
        self.model.apply(op,tid,qpc,fields)
        after={k:self.model.live.get(k) for k in before}
        for k,b in before.items():
            if b is not None and (after[k] is None or after[k].generation!=b.generation):
                self.require(b.generation not in self.deaths,'generation retired twice')
                self.deaths[b.generation]=Death(qpc,'HeapFree' if op==36 else 'HeapRealloc',k[1],b.size)
        for k,b in after.items():
            if b is not None and b.generation not in self.births:
                self.births[b.generation]=Birth(qpc,k[1],b.size,tid,False)
        if op==34:
            child=after[key];parent=before[oldkey]
            # A moved HeapReAlloc summary follows its primitive HeapFree; the
            # old address might already have been reused by another thread.
            if before[key] is not None and key!=oldkey:
                parent_generation=oldfree.generation if oldfree else None
            else:parent_generation=parent.generation if parent else None
            if parent_generation is not None:
                self.require(parent_generation!=child.generation and parent_generation not in self.successors,'ambiguous realloc successor')
                self.successors[parent_generation]=(child.generation,qpc)

    def compare(self,earlier,later,start,end):
        a={b.generation:(address,b) for address,b in earlier.items()};b={v.generation:(address,v) for address,v in later.items()}
        retained=a.keys()&b.keys();retired=a.keys()-b.keys();new=b.keys()-a.keys()
        self.require(all(a[g][0]==b[g][0] and a[g][1].size==b[g][1].size for g in retained),'retained generation identity changed')
        self.require(all(g in self.deaths and start<self.deaths[g].qpc<=end for g in retired),'retirement witness missing/outside epoch')
        self.require(all(g in self.births and start<self.births[g].qpc<=end for g in new),'birth witness missing/outside epoch')
        dispositions={reason:sum(self.deaths[g].reason==reason for g in retired) for reason in ['HeapFree','HeapRealloc','HeapDestroy']}
        alive_successors=0;successor_seen=0
        for g in retired:
            current=g;seen=set()
            while current in self.successors and self.successors[current][1]<=end:
                self.require(current not in seen,'reallocation successor cycle');seen.add(current);current=self.successors[current][0]
            if seen:successor_seen+=1
            if seen and current in b:alive_successors+=1
        born=[g for g,v in self.births.items() if start<v.qpc<=end]
        died=[g for g,v in self.deaths.items() if start<v.qpc<=end]
        self.require(len(earlier)+len(born)-len(died)==len(later),'interval block conservation')
        self.require(sum(v.size for v in earlier.values())+sum(self.births[g].size for g in born)-sum(self.deaths[g].size for g in died)==sum(v.size for v in later.values()),'interval byte conservation')
        return dict(InitialBlocks=len(a),FinalBlocks=len(b),SameGenerationSurvivors=len(retained),SameGenerationSurvivorBytes=sum(a[g][1].size for g in retained),RetiredGenerations=len(retired),RetirementEvents=dispositions,
            RetiredWithObservedReallocSuccessor=successor_seen,RetiredWithLiveReallocSuccessor=alive_successors,NewLiveGenerations=len(new),AllIntervalBirths=len(born),AllIntervalRetirements=len(died),BlockAndByteConservation=True)
