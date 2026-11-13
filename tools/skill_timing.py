import json,collections,statistics,sys
def varint(b,o):
    r=s=0
    while True:
        c=b[o];o+=1;r|=(c&0x7f)<<s;s+=7
        if c<0x80:return r,o
tails=collections.defaultdict(collections.Counter); ends=collections.defaultdict(list); fx=collections.defaultdict(list); lastb=collections.Counter(); cut=collections.Counter()
for p in sys.argv[2:]:
    s2c=[m for m in (json.loads(l) for l in open(p)) if m['dir']=='s2c' and m.get('body')]
    for i,m in enumerate(s2c):
        if m['op']!='0x3801' or m['len']!=9: continue
        b=bytes.fromhex(m['body']); sk=int.from_bytes(b[5:9],'little')
        if int.from_bytes(b[3:5],'little')!=0: continue
        st=None
        for j in range(i-1,max(0,i-6),-1):
            x=s2c[j]
            if x['op']=='0x3802':
                bb=bytes.fromhex(x['body']);k,o=varint(bb,2)
                if int.from_bytes(bb[o+1:o+5],'little')==sk: st=(j,x,k,bb[o+5]);break
        if not st: continue
        j,x,me,uid=st
        nextstart=None
        for y in s2c[j+1:j+1500]:
            if y['ms']-x['ms']>6000: break
            bb=bytes.fromhex(y['body'])
            if y['op']=='0x3802' and nextstart is None:
                k,o=varint(bb,2)
                if k==me: nextstart=y['ms']
            if y['op']=='0x3806':
                k,o=varint(bb,2)
                if k==me and int.from_bytes(bb[o:o+4],'little')==sk and bb[o+4]==uid:
                    d=y['ms']-x['ms']; tail=bb[o+5]
                    early = nextstart is not None and nextstart<=y['ms']
                    lastb[(tail,early)]+=1; tails[sk][tail]+=1
                    if not early: ends[sk].append(d)   # natural end, not cut by the next cast
                    break
        for y in s2c[j+1:j+800]:
            if y['ms']-x['ms']>6000: break
            if y['op']=='0x3804':
                bb=bytes.fromhex(y['body']); t,o=varint(bb,2); o+=2; a,o=varint(bb,o)
                if a==me and int.from_bytes(bb[o:o+4],'little')==sk and bb[o+4]==uid: fx[sk].append(y['ms']-x['ms']); break
print('end tail byte (value, cut by next cast):',lastb.most_common())
out={str(sk):{'end_ms':int(statistics.median(d)),'samples':len(d),'end_tail':tails[sk].most_common(1)[0][0],**({'effect_ms':int(statistics.median(fx[sk]))} if fx.get(sk) else {})} for sk,d in sorted(ends.items())}
allend=[v for d in ends.values() for v in d]
res={'source':'official recordings 2026-11-06 (capture-13700-*): median ms from SkillStart_NT 0x3802 to the caster\'s natural SkillEnd 0x3806 (not cut by the next cast) and to its first SkillEffect_NT 0x3804; end_tail = the usual last byte of each skill 0x3806 (12 on chain/combo skills); tools/skill_timing.py','default_end_ms':int(statistics.median(allend)),'skills':out}
json.dump(res,open(sys.argv[1],'w'),indent=1)
print(len(out),'skills, default',res['default_end_ms'])
