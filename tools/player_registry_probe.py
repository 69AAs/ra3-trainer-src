
"""玩家名册探针（**只读**）：找"玩家的生产设施名册"在哪，并看被夺舍的那栋楼在不在里面。

背景（实测）：夺舍后用宿主侧补 `U+0x414`（条目）**不足以**让界面上能排产；
但那栋楼自己产出的单位**已经是我的** ⇒ owner 生效、产出继承归属；缺的是**界面/玩家的设施名册**登记。

做法：定位"我"的玩家对象，扫它内部所有像对象指针的字段，逐个看目标对象的 owner/类型，
再专门查"被夺舍的那栋楼"有没有出现在里面。
"""
import ctypes, ctypes.wintypes as wt, struct, sys, os, glob, json
TARGET="ra3_1.12.game"; PLAYER_LIST=0x00CE8C9C; TABLE_OFF=0x30; P_ENTRY=0xAC
k32=ctypes.WinDLL('kernel32', use_last_error=True)
k32.ReadProcessMemory.restype=wt.BOOL
k32.ReadProcessMemory.argtypes=[wt.HANDLE,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_size_t,ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualQueryEx.restype=ctypes.c_size_t
class MBI(ctypes.Structure):
    _fields_=[("BaseAddress",ctypes.c_void_p),("AllocationBase",ctypes.c_void_p),("AllocationProtect",wt.DWORD),("__a",wt.DWORD),("RegionSize",ctypes.c_size_t),("State",wt.DWORD),("Protect",wt.DWORD),("Type",wt.DWORD),("__b",wt.DWORD)]
class PE32(ctypes.Structure):
    _fields_=[("dwSize",wt.DWORD),("cntUsage",wt.DWORD),("th32ProcessID",wt.DWORD),("th32DefaultHeapID",ctypes.POINTER(ctypes.c_ulong)),("th32ModuleID",wt.DWORD),("cntThreads",wt.DWORD),("th32ParentProcessID",wt.DWORD),("pcPriClassBase",ctypes.c_long),("dwFlags",wt.DWORD),("szExeFile",ctypes.c_char*260)]
snap=k32.CreateToolhelp32Snapshot(0x2,0); e=PE32(); e.dwSize=ctypes.sizeof(e)
ok=k32.Process32First(snap,ctypes.byref(e)); pid=None
while ok:
    if e.szExeFile.decode('latin1','replace').rstrip('\x00').lower()==TARGET.lower(): pid=e.th32ProcessID; break
    ok=k32.Process32Next(snap,ctypes.byref(e))
k32.CloseHandle(snap)
if pid is None: raise SystemExit("找不到 %s（游戏没在跑）"%TARGET)
h=k32.OpenProcess(0x0400|0x0010,False,pid)
def u32(a):
    b=ctypes.create_string_buffer(4); got=ctypes.c_size_t()
    if not k32.ReadProcessMemory(h,ctypes.c_void_p(a&0xFFFFFFFF),ctypes.byref(b),4,ctypes.byref(got)) or got.value!=4: return None
    return struct.unpack('<I',b.raw)[0]
def span(a,n):
    out=b''; cur=a
    while len(out)<n:
        chunk=min(0x1000,n-len(out))
        mbi=MBI()
        if not k32.VirtualQueryEx(h,ctypes.c_void_p(cur),ctypes.byref(mbi),ctypes.sizeof(mbi)): break
        if mbi.State!=0x1000 or (mbi.Protect&0x101)!=0: break
        base=mbi.BaseAddress or 0
        if not (base <= cur and cur+chunk <= base+mbi.RegionSize): break
        b=ctypes.create_string_buffer(chunk); got=ctypes.c_size_t()
        if not k32.ReadProcessMemory(h,ctypes.c_void_p(cur),ctypes.byref(b),chunk,ctypes.byref(got)) or got.value!=chunk: break
        out+=b.raw; cur+=chunk
    return out
# 目标对象：最近一次补写快照里的那个
snaps=sorted(glob.glob(os.path.join(os.path.dirname(os.path.abspath(__file__)),"..","artifacts","entry-fix-snapshots","entry-*.json")))
tgt_obj=None
if snaps: tgt_obj=int(json.load(open(snaps[-1],encoding="utf-8"))["obj"],16)
# 我 = 条目匹配的那个玩家；否则取玩家表第 0 槽
glob_addr=u32(PLAYER_LIST); me=None; entry=None
for i in range(10):
    p=u32(glob_addr+TABLE_OFF+i*4) if glob_addr else None
    if not p: continue
    if tgt_obj is not None and u32(tgt_obj+0x418)==p: me=p; entry=u32(p+P_ENTRY); break
print("PID=%d 被查对象=0x%08X 我的玩家=0x%08X 我的条目=0x%08X" % (pid, tgt_obj or 0, me or 0, entry or 0))
if not me: raise SystemExit("定位不到我的玩家对象")
body=span(me, 0x8000)
print("我的玩家对象体内可读 %d 字节（扫 0x8000）" % len(body))
# ① 直接找"指向那栋楼"的字段
hits=[off for off in range(0,len(body)-4,4) if struct.unpack('<I',body[off:off+4])[0]==tgt_obj]
print("① 玩家对象里指向被夺舍那栋楼 0x%08X 的字段: %s" % (tgt_obj or 0, " ".join("+0x%X"%o for o in hits[:20]) or "无"))
# ② 统计"玩家对象里的对象指针"都指向谁（按 owner/type 归类），看有没有一份名册
seen={}; objects=[]
for off in range(0,len(body)-4,4):
    v=struct.unpack('<I',body[off:off+4])[0]
    if not (0x02000000 <= v <= 0x20000000) or v%4: continue
    o=u32(v+0x418); t=u32(v+4); ty=u32(t+8) if t else None
    if o is None: continue
    objects.append((off,v,o,ty))
mine=[x for x in objects if x[2]==me]
other=[x for x in objects if x[2]!=me]
print("② 玩家对象里像对象的指针：共 %d 个，其中 owner=我 的 %d 个" % (len(objects), len(mine)))
print("   我名下的（偏移→对象→类型）：")
for off,v,o,ty in mine[:24]:
    tag=" ★就是被夺舍那栋" if v==tgt_obj else ""
    print("      +0x%04X → 0x%08X type=0x%08X%s" % (off, v, ty or 0, tag))
if other[:6]:
    print("   非我名下（前 6）：")
    for off,v,o,ty in other[:6]:
        print("      +0x%04X → 0x%08X owner=0x%08X type=0x%08X" % (off, v, o, ty or 0))


# ============ 正对照模式（--own）：拿"能排产的楼" vs "夺舍来不能排产的楼" ============
if "--own" in sys.argv:
    def selected():
        root=u32(0x400000+0x8DB73C); node=u32(root+0x50) if root else None
        wrap=u32(node+8) if node else None
        return u32(wrap+0x138) if wrap else None
    def regions_from(a, cap=0x40000):
        out=[]; cur=a; end=a+cap
        while cur < end:
            mbi=MBI()
            if not k32.VirtualQueryEx(h,ctypes.c_void_p(cur),ctypes.byref(mbi),ctypes.sizeof(mbi)): break
            base=mbi.BaseAddress or 0; size=mbi.RegionSize
            if mbi.State!=0x1000 or (mbi.Protect&0x101)!=0 or mbi.Type!=0x20000: break   # 只走 MEM_PRIVATE（对象所在的堆块）
            out.append((cur, min(base+size, end)-cur))
            cur = base+size
        return out
    own = selected()
    if not own: raise SystemExit("先在你**能排产**的那栋建筑上点一下（保持选中）再跑 --own")
    cap = tgt_obj
    my = u32(own+0x418); myentry=u32(my+P_ENTRY) if my else None
    print("正对照：能排产的楼 U_own=0x%08X（owner=0x%08X，条目=0x%08X）" % (own, my or 0, u32(own+0x414) or 0))
    print("被夺舍的楼 U_cap=0x%08X（owner=0x%08X，条目=0x%08X）" % (cap or 0, u32(cap+0x418) or 0 if cap else 0, u32(cap+0x414) or 0 if cap else 0))
    body=b''.join(span(a,n) for a,n in regions_from(my))
    print("\n我的玩家对象（含其私有堆块）可读 %d 字节" % len(body))
    for name,val in (("能排产的楼", own), ("被夺舍的楼", cap)):
        if not val: continue
        hits=[off for off in range(0,len(body)-4,4) if struct.unpack('<I',body[off:off+4])[0]==val]
        print("  玩家对象里指向【%s】(0x%08X) 的字段: %s" % (name, val, " ".join("+0x%X"%o for o in hits[:16]) or "无"))
    # 找"连续一族对象指针"（名册的特征）：连续 ≥4 个 dword 都指向 owner==我的对象
    run=[]; runs=[]
    for off in range(0,len(body)-4,4):
        v=struct.unpack('<I',body[off:off+4])[0]
        okp=False
        if 0x02000000 <= v <= 0x20000000 and v%4==0 and u32(v+0x418)==my:
            okp=True
        if okp: run.append((off,v))
        else:
            if len(run)>=4: runs.append(run)
            run=[]
    if len(run)>=4: runs.append(run)
    print("  连续≥4 的『我名下对象指针』族：%d 处" % len(runs))
    for r in runs[:4]:
        print("     +0x%X 起 %d 个：" % (r[0][0], len(r)))
        for off,v in r[:8]:
            tt=u32(u32(v+4)+8) if u32(v+4) else None
            tag=""
            if v==own: tag=" ←能排产的那栋"
            if v==cap: tag=" ←被夺舍那栋"
            print("        +0x%04X → 0x%08X type=0x%08X%s" % (off, v, tt or 0, tag))
    # 结构位差：0x000..0x800 里"一个是指针、另一个是 0"的偏移（缺登记的特征）
    if cap:
        so, sc = span(own,0x800), span(cap,0x800)
        diffs=[]
        for off in range(0,min(len(so),len(sc))-4,4):
            a=struct.unpack('<I',so[off:off+4])[0]; b=struct.unpack('<I',sc[off:off+4])[0]
            pa = 0x02000000<=a<=0x20000000; pb = 0x02000000<=b<=0x20000000
            if pa!=pb: diffs.append((off,a,b))
        print("\n结构位差（0x000..0x800 内『一边是指针、另一边是 0』）：%d 处" % len(diffs))
        for off,a,b in diffs[:24]:
            print("   +0x%03X  能排产=0x%08X  被夺舍=0x%08X  %s" % (off,a,b,"(能排产有/被夺舍无)" if a and not b else "(被夺舍有/能排产无)"))
    raise SystemExit(0)
