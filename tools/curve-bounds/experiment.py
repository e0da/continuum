"""Exact rational representation experiment; not a floating-point qualification."""
from fractions import Fraction as F
import json
from pathlib import Path
import argparse

def add(x,y):return x[0]+y[0],x[1]+y[1]
def scale(a,x):return (a*x[0],a*x[1]) if a>=0 else (a*x[1],a*x[0])
def square(x):return (0 if x[0]<=0<=x[1] else min(x[0]**2,x[1]**2),max(x[0]**2,x[1]**2))
def bounds(p,v,a,lo,hi):
    at=lambda t:p+v*t+a*t*t/2
    naive=add(add((p,p),scale(v,(lo,hi))),scale(a/2,square((lo,hi))))
    mid=(lo+hi)/2;h=(hi-lo)/2
    centered=add(add((at(mid),at(mid)),scale(v+a*mid,(-h,h))),scale(a/2,square((-h,h))))
    controls=(at(lo),at(lo)+(v+a*lo)*(hi-lo)/2,at(hi))
    bernstein=(min(controls),max(controls))
    values=[at(lo),at(hi)]
    if a and lo<=-v/a<=hi:values.append(at(-v/a))
    exact=(min(values),max(values))
    for x in (naive,centered,bernstein):
        if not (x[0]<=exact[0] and x[1]>=exact[1]):raise ArithmeticError("Enclosure omitted an exact extremum")
    intersection=(max(naive[0],centered[0],bernstein[0]),min(naive[1],centered[1],bernstein[1]))
    if not (intersection[0]<=exact[0] and intersection[1]>=exact[1]):raise ArithmeticError("Intersection omitted an exact extremum")
    return dict(naive=naive,centered=centered,bernstein=bernstein,intersection=intersection,exact=exact)

rows=[]
for label,p,v,a,lo,hi in [('turn',100,-20,2,0,20),('before-turn',100,-20,2,0,8),('across-turn',100,-20,2,9,11),('after-turn',100,-20,2,12,20),('linear',7,-3,0,0,20),('concave',-100,20,-2,0,20)]:
    params=list(map(F,(p,v,a,lo,hi)));b=bounds(*params)
    rows.append(dict(name=label,inputs=list(map(str,params)),bounds={k:list(map(str,val)) for k,val in b.items()}))
count=0
for p in [-100,0,100]:
 for v in [-20,0,20]:
  for a in [-2,0,2]:
   for lo in range(20):
    for hi in range(lo,21):bounds(*map(F,(p,v,a,lo,hi)));count+=1
r=dict(schema='ksp-continuum-exact-curve-bound-toy/v1',qualified=True,arithmetic='exact rational',casesChecked=count,cases=rows,floatingPointQualified=False,productionImplemented=False,performanceMeasured=False)
parser=argparse.ArgumentParser(description=__doc__)
parser.add_argument('--output', required=True, type=Path)
out=parser.parse_args().output
with out.open('x') as f:json.dump(r,f,indent=2);f.write('\n')
print(json.dumps({k:r[k] for k in ('qualified','casesChecked','floatingPointQualified','productionImplemented','performanceMeasured')}))
