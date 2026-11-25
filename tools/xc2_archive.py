"""Local ARH1 reader. Format reference: RoccoDev/ard-tools docs/arh1.md (MIT/Apache-2.0).
Extracted game data must stay in this workspace.
"""
from pathlib import Path
import struct
import zlib

class Archive:
    def __init__(self, folder):
        self.folder = Path(folder)
        self.raw = bytearray((self.folder/'bf2.arh').read_bytes())
        assert self.raw[:4] == b'arh1'
        self.count, so, sl, po, pl, self.fo, self.fn, key = struct.unpack_from('<8I', self.raw, 8)
        key = (key ^ 0xf3f35353).to_bytes(4, 'little')
        def decode(off, size):
            b = bytearray(self.raw[off:off+size])
            for i in range(size//4*4): b[i] ^= key[i%4]
            return b
        self.strings, self.nodes = decode(so, sl), decode(po, pl)

    def metadata(self, fid):
        off, size, expanded, flags, stored = struct.unpack_from('<Q4I', self.raw, self.fo+fid*24)
        assert stored == fid
        return dict(fid=fid, offset=off, size=size, expanded=expanded, flags=flags)

    def entries(self):
        for i in range(self.count):
            nxt, prev = struct.unpack_from('<ii', self.nodes, i*8)
            if nxt >= 0 or prev < 0 or prev >= self.count: continue
            chars=[]; cur=i
            for _ in range(2048):
                parent=struct.unpack_from('<i', self.nodes, cur*8+4)[0]
                if parent < 0: break
                if parent >= self.count: chars=[];break
                base=struct.unpack_from('<i', self.nodes, parent*8)[0]
                c=base ^ cur
                if not 0 < c < 128: chars=[];break
                chars.append(c);cur=parent
            if not chars or not 0 < -nxt < len(self.strings): continue
            end=self.strings.index(0,-nxt)
            name=bytes(reversed(chars))+self.strings[-nxt:end]
            fid=struct.unpack_from('<I', self.strings,end+1)[0]
            if fid >= self.fn: continue
            yield name.decode('ascii'),self.metadata(fid)

    def lookup(self, name):
        index=0;tail=name.encode('ascii')
        while True:
            nxt,_=struct.unpack_from('<ii',self.nodes,index*8)
            if nxt < 0: break
            if not tail: return None
            child=nxt ^ tail[0]
            if not 0 <= child < self.count: return None
            if struct.unpack_from('<i',self.nodes,child*8+4)[0] != index:return None
            index,tail=child,tail[1:]
        end=self.strings.index(0,-nxt)
        if self.strings[-nxt:end] != tail:return None
        return self.metadata(struct.unpack_from('<I',self.strings,end+1)[0])

    def read(self, entry):
        with (self.folder/'bf2.ard').open('rb') as f:
            f.seek(entry['offset']); b=f.read(entry['size']+48)
        if entry['flags'] & 2:
            assert b[:4] == b'xbc1' and struct.unpack_from('<I',b,4)[0] == 1
            expanded,compressed=struct.unpack_from('<II',b,8)
            b=zlib.decompress(b[48:48+compressed])
            assert len(b)==expanded==entry['expanded']
        else: b=b[:entry['size']]
        return b
