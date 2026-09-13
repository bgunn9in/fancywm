"""Native heap event replay, preserving realloc's measured primitive events."""
from collections import namedtuple
Block=namedtuple('Block','size stack generation opcode qpc tid')
Freed=namedtuple('Freed','size generation qpc tid')
class Replay:
    def __init__(self):
        self.live={};self.freed={};self.generation=0;self.unknown_frees=0;self.destroyed=0;self.aliases=[]
    @staticmethod
    def require(ok,message):
        if not ok:raise ValueError(message)
    def destroy(self,heap):
        keys=[k for k in self.live if k[0]==heap]
        for key in keys:del self.live[key]
        self.destroyed+=len(keys)
    def apply(self,op,tid,qpc,fields):
        heap,address=fields[:2];key=(heap,address)
        if op==36:
            old=self.live.pop(key,None)
            if old is None:self.unknown_frees+=1
            self.freed[key]=Freed(old.size if old else None,old.generation if old else None,qpc,tid)
            return
        if op==34:
            oldkey=(heap,fields[2]);new=self.live.get(key);free=self.freed.get(oldkey)
            if key!=oldkey and new is not None:
                self.require(new.opcode==33 and new.tid==tid and new.size==fields[3] and free is not None and free.tid==tid and new.qpc<free.qpc<qpc,'unexplained realloc destination already live')
                self.require(free.size is None or free.size==fields[4],'nested realloc original size')
                # The freed address can already have a new allocation on a
                # different thread before the outer Realloc summary arrives.
                oldnow=self.live.get(oldkey)
                self.require(oldnow is None or oldnow.qpc>free.qpc,'nested realloc original generation still live')
                self.aliases.append(dict(Heap=heap,Old=oldkey[1],New=address,AllocQpc=new.qpc,FreeQpc=free.qpc,ReallocQpc=qpc,Tid=tid,OldSizeKnown=free.size is not None,OriginalAddressAlreadyReused=oldnow is not None))
                self.live[key]=Block(fields[3],(tid,qpc),new.generation,34,qpc,tid)
                return
            old=self.live.pop(oldkey,None)
            if old is not None:self.require(old.size==fields[4],'realloc original size identity')
            else:self.unknown_frees+=1
        self.require(key not in self.live,'native address reuse without free/reallocation')
        self.generation+=1
        self.live[key]=Block(fields[2] if op==33 else fields[3],(tid,qpc),self.generation,op,qpc,tid)
