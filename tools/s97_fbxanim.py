"""FBX <-> Unity rotation bridge for the b2 SOMA rig, plus curve access.

Convention FITTED over 78 bones in conv.py, not assumed:
    order XYZ (R = Rz*Ry*Rx),  unity_q = (qx, -qy, -qz, qw)  of the FBX-space quaternion.
Worst residual over the whole rig: 0.031 deg.
"""
import s97_fbxbin as fbxbin, collections, math
import numpy as np

def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return np.array([aw*bx+ax*bw+ay*bz-az*by,
                     aw*by-ax*bz+ay*bw+az*bx,
                     aw*bz+ax*by-ay*bx+az*bw,
                     aw*bw-ax*bx-ay*by-az*bz])

def _ax(i,deg):
    r=math.radians(deg)/2.0; s=math.sin(r); v=[0.,0.,0.]; v[i]=s
    return np.array([v[0],v[1],v[2],math.cos(r)])

def euler_to_fbxq(e):
    """e = (X,Y,Z) degrees, FBX eEulerXYZ."""
    q=np.array([0.,0.,0.,1.])
    for i in (0,1,2): q=qmul(_ax(i,e[i]), q)
    return q

def fbxq_to_euler(q):
    x,y,z,w=q
    # rotation matrix rows we need for the Rz*Ry*Rx extraction
    r20=2*(x*z-w*y); r21=2*(y*z+w*x); r22=1-2*(x*x+y*y)
    r10=2*(x*y+w*z); r00=1-2*(y*y+z*z)
    ey=math.asin(max(-1.0,min(1.0,-r20)))
    if abs(r20) < 0.999999:
        ex=math.atan2(r21,r22); ez=math.atan2(r10,r00)
    else:                                   # gimbal lock: fold Z into X
        ex=math.atan2(-2*(y*z-w*x), 1-2*(x*x+z*z)); ez=0.0
    return (math.degrees(ex), math.degrees(ey), math.degrees(ez))

def unity_to_fbxq(q):  return np.array([ q[0], -q[1], -q[2], q[3]])
def fbxq_to_unity(q):  return np.array([ q[0], -q[1], -q[2], q[3]])

def qang(a,b):
    d=abs(float(np.dot(a,b)))
    return 2*math.degrees(math.acos(min(1.0,d)))

class Fbx:
    def __init__(self, path):
        self.path=path
        self.raw=open(path,'rb').read()
        self.tree=fbxbin.parse(self.raw)
        objs=[r for r in self.tree['roots'] if r.name==b'Objects'][0]
        conns=[r for r in self.tree['roots'] if r.name==b'Connections'][0]
        self.byid={n.props[0][1]: n for n in objs.children if n.props and n.props[0][0]==b'L'}
        self.op=collections.defaultdict(list)
        for c in conns.children:
            ps=[x[1] for x in c.props]
            if ps[0]==b'OP': self.op[ps[2]].append((ps[1], ps[3]))
        self.model={}
        for i,n in self.byid.items():
            if n.name==b'Model': self.model[n.props[1][1].split(b'\x00')[0].decode()]=i

    def rot_curves(self, bone):
        """-> {b'd|X': AnimationCurve node, ...} for that bone's Lcl Rotation."""
        mid=self.model[bone]
        for cid,prop in self.op[mid]:
            n=self.byid.get(cid)
            if n is None or n.name!=b'AnimationCurveNode' or prop!=b'Lcl Rotation': continue
            out={}
            for ccid,cprop in self.op[cid]:
                cn=self.byid.get(ccid)
                if cn is not None and cn.name==b'AnimationCurve': out[cprop]=cn
            return out
        raise KeyError(bone)

    def rot_values(self, bone):
        c=self.rot_curves(bone)
        return np.array([c[b'd|X'].find(b'KeyValueFloat').props[0][1],
                         c[b'd|Y'].find(b'KeyValueFloat').props[0][1],
                         c[b'd|Z'].find(b'KeyValueFloat').props[0][1]]).T   # (frames,3)
