"""Minimal FBX binary (7.x, 32-bit offset) reader/writer.

Contract: parse(bytes) -> tree; serialize(tree) -> bytes; and serialize(parse(b)) == b
must hold on the untouched source before this module is allowed to change anything.
"""
import struct, zlib

MAGIC = b"Kaydara FBX Binary  \x00\x1a\x00"

class Node:
    __slots__ = ("name", "props", "children", "term")
    def __init__(self, name=b"", props=None, children=None):
        self.name = name; self.props = props or []; self.children = children or []
        self.term = False   # did the source emit a null record after this node's children?
    def __repr__(self):
        return "<%s props=%d kids=%d>" % (self.name.decode('ascii','replace'),
                                          len(self.props), len(self.children))
    def find(self, name):
        for c in self.children:
            if c.name == name: return c
        return None
    def findall(self, name):
        return [c for c in self.children if c.name == name]

# property: (typecode:bytes1, value, raw_encoding_info)
def _read_prop(b, o):
    t = b[o:o+1]; o += 1
    if t == b'Y': v = struct.unpack_from('<h', b, o)[0]; o += 2; return (t, v, None), o
    if t == b'C': v = b[o]; o += 1; return (t, v, None), o
    if t == b'I': v = struct.unpack_from('<i', b, o)[0]; o += 4; return (t, v, None), o
    if t == b'F': v = struct.unpack_from('<f', b, o)[0]; o += 4; return (t, v, None), o
    if t == b'D': v = struct.unpack_from('<d', b, o)[0]; o += 8; return (t, v, None), o
    if t == b'L': v = struct.unpack_from('<q', b, o)[0]; o += 8; return (t, v, None), o
    if t in (b'S', b'R'):
        n = struct.unpack_from('<I', b, o)[0]; o += 4
        v = b[o:o+n]; o += n; return (t, v, None), o
    if t in (b'f', b'd', b'l', b'i', b'b'):
        cnt, enc, clen = struct.unpack_from('<III', b, o); o += 12
        raw = b[o:o+clen]; o += clen
        data = zlib.decompress(raw) if enc == 1 else raw
        fmt = {b'f': 'f', b'd': 'd', b'l': 'q', b'i': 'i', b'b': 'B'}[t]
        arr = list(struct.unpack('<%d%s' % (cnt, fmt), data))
        # keep enc so a re-serialize can reproduce the original bytes exactly
        return (t, arr, (enc, raw)), o
    raise ValueError("unknown property type %r at %d" % (t, o - 1))

def _read_node(b, o):
    end, nprop, plen, nlen = struct.unpack_from('<IIIB', b, o); o += 13
    if end == 0 and nprop == 0 and plen == 0 and nlen == 0:
        return None, o                      # null terminator
    name = b[o:o+nlen]; o += nlen
    props = []
    for _ in range(nprop):
        p, o = _read_prop(b, o)
        props.append(p)
    kids = []
    term = False
    if o < end:
        while True:
            k, o = _read_node(b, o)
            if k is None: term = True; break
            kids.append(k)
    assert o == end, "node %r ended at %d, header said %d" % (name, o, end)
    n = Node(name, props, kids); n.term = term
    return n, o

def parse(b):
    assert b[:len(MAGIC)] == MAGIC, "not an FBX binary"
    ver = struct.unpack_from('<I', b, 23)[0]
    o = 27
    roots = []
    while True:
        n, o = _read_node(b, o)
        if n is None: break
        roots.append(n)
    footer = b[o:]
    return {"version": ver, "roots": roots, "footer": footer}

def _write_prop(p):
    t, v, extra = p
    if t == b'Y': return t + struct.pack('<h', v)
    if t == b'C': return t + struct.pack('<B', v)
    if t == b'I': return t + struct.pack('<i', v)
    if t == b'F': return t + struct.pack('<f', v)
    if t == b'D': return t + struct.pack('<d', v)
    if t == b'L': return t + struct.pack('<q', v)
    if t in (b'S', b'R'): return t + struct.pack('<I', len(v)) + v
    fmt = {b'f': 'f', b'd': 'd', b'l': 'q', b'i': 'i', b'b': 'B'}[t]
    enc, raw = extra if extra else (1, None)
    data = struct.pack('<%d%s' % (len(v), fmt), *v)
    if extra is not None and raw is not None:
        # unchanged array: reuse the ORIGINAL compressed bytes so the file round-trips
        # byte-for-byte. A caller that edits values must clear extra to force a re-deflate.
        return t + struct.pack('<III', len(v), enc, len(raw)) + raw
    if enc == 1:
        raw = zlib.compress(data)
    else:
        raw = data
    return t + struct.pack('<III', len(v), enc, len(raw)) + raw

def _write_node(n, offset):
    body = b"".join(_write_prop(p) for p in n.props)
    head_len = 13 + len(n.name)
    kids = b""
    base = offset + head_len + len(body)
    for c in n.children:
        kids += _write_node(c, base + len(kids))
    if n.term or n.children:
        kids += b"\x00" * 13
    end = offset + head_len + len(body) + len(kids)
    return (struct.pack('<IIIB', end, len(n.props), len(body), len(n.name))
            + n.name + body + kids)

def serialize(tree):
    out = MAGIC + struct.pack('<I', tree["version"])
    o = len(out)
    parts = []
    for r in tree["roots"]:
        s = _write_node(r, o)
        parts.append(s); o += len(s)
    parts.append(b"\x00" * 13)
    return out + b"".join(parts) + tree["footer"]
