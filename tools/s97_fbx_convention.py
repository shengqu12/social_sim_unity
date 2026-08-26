"""Identify the FBX->Unity rotation convention by FIT over all 79 bones, not by assumption."""
import s97_fbxbin as fbxbin, csv, math, itertools
import numpy as np

FBX='/home/sheng/Desktop/research/social_navigation/social_sim_unity/Assets/PedestrianAssets/Kimodo/Resources/kimodo_b2_surprised.fbx'
REST='/mnt/ssd/Social_Navigation/sandbox_s72_nextgen/s97/data/src_rest.csv'

def axis_q(ax, deg):
    r=math.radians(deg)/2.0; s=math.sin(r)
    v=[0.0,0.0,0.0]; v[ax]=s
    return np.array([v[0],v[1],v[2],math.cos(r)])

def qmul(a,b):
    ax,ay,az,aw=a; bx,by,bz,bw=b
    return np.array([aw*bx+ax*bw+ay*bz-az*by,
                     aw*by-ax*bz+ay*bw+az*bx,
                     aw*bz+ax*by-ay*bx+az*bw,
                     aw*bw-ax*bx-ay*by-az*bz])

def euler_q(order, e):
    q=np.array([0.,0.,0.,1.])
    for ax in order:            # applied in this sequence, each pre-multiplying
        q=qmul(axis_q(ax, e[ax]), q)
    return q

def fbx_rest():
    t=fbxbin.parse(open(FBX,'rb').read())
    objs=[r for r in t['roots'] if r.name==b'Objects'][0]
    out={}
    for m in objs.children:
        if m.name!=b'Model': continue
        nm=m.props[1][1].split(b'\x00')[0].decode()
        p70=m.find(b'Properties70'); rot=[0.,0.,0.]
        if p70:
            for pp in p70.children:
                if pp.props[0][1]==b'Lcl Rotation':
                    rot=[float(x[1]) for x in pp.props[4:7]]
        out[nm]=rot
    return out

def unity_rest():
    out={}
    with open(REST) as f:
        for r in csv.DictReader(f):
            out[r['name']]=np.array([float(r['qx']),float(r['qy']),float(r['qz']),float(r['qw'])])
    return out

fb=fbx_rest(); un=unity_rest()
common=[k for k in fb if k in un]
print("bones matched:", len(common), "of", len(fb))

best=[]
orders=list(itertools.permutations([0,1,2]))
for order in orders:
    for sx,sy,sz in itertools.product([1,-1],repeat=3):
        for conj in (False,True):
            worst=0.0
            for k in common:
                e=fb[k]
                q=euler_q(order, e)
                if conj: q=np.array([-q[0],-q[1],-q[2],q[3]])
                q=np.array([sx*q[0], sy*q[1], sz*q[2], q[3]])
                u=un[k]
                d=abs(float(np.dot(q,u)))
                ang=2*math.degrees(math.acos(min(1.0,d)))
                worst=max(worst,ang)
            best.append((worst, order, (sx,sy,sz), conj))
best.sort()
for b in best[:4]:
    print("worst %.6f deg  order=%s signs=%s conj=%s" % b)
