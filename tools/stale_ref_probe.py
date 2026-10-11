
"""残留引用探针（**只读**）：查"被夺舍的对象里，还有哪些字段仍指向旧主"。

为什么：实测只补 `U+0x414`（条目）**不足以**让夺舍过来的生产建筑恢复生产 ⇒ 缺口不止一个字段。
最便宜的判据：拿玩家表定位"旧主"和"我"两个玩家对象，再在对象体内扫出所有指向它们的字段。
"""
import ctypes, ctypes.wintypes as wt, struct, sys, os, glob, json
TARGET="ra3_1.12.game"; BASE=0x400000; ROOT_RVA=0x8DB73C
PLAYER_LIST=0x00CE8C9C; TABLE_OFF=0x30; P_ENTRY=0xAC
k32=ctypes.WinDLL('kernel32', use_last_error=True)
k32.ReadProcessMemory.restype=wt.BOOL
k32.ReadProcessMemory.argtypes=[wt.HANDLE,ctypes.c_void_p,ctypes.c_void_p,ctypes.c_size_t,ctypes.POINTER(ctypes.c_size_t)]
k32.VirtualQueryEx.restype=ctypes.c_size_t
class MBI(ctypes.Structure):
    _fields_=[("BaseAddress",ctypes.c_void_p),("AllocationBase",ctypes.c_void_p),("AllocationProtect",wt.DWORD),("__a",wt.DWORD),("RegionSize",ctypes.c_size_t),("State",wt.DWORD),("Protect",wt.DWORD),("Type",wt.DWORD),("__b",wt.DWORD)]
class PE32(ctypes.Structure):
    _fields_=[("dwSize",wt.DWORD),("cntUsage",wt.DWORD),("th32ProcessID",wt.DWORD),("th32DefaultHeapID",ctypes.POINTER(ctypes.c_ulong)),("th32ModuleID",wt.DWORD),("cntThreads",wt.DWORD),("th32ParentProcessID",wt.DWORD),("pcPriClassBase",ctypes.c_long),("dwFlags",wt.DWORD),("szExeFile",ctypes.c_char*260)]
def find_pid():
    snap=k32.CreateToolhelp32Snapshot(0x2,0); e=PE32(); e.dwSize=ctypes.sizeof(e)
    ok=k32.Process32First(snap,ctypes.byref(e)); pid=None
    while ok:
        if e.szExeFile.decode('latin1','replace').rstrip('\x00').lower()==TARGET.lower(): pid=e.th32ProcessID; break
        ok=k32.Process32Next(snap,ctypes.byref(e))
    k32.CloseHandle(snap); return pid
pid=find_pid()
if pid is None: raise SystemExit("找不到 %s（游戏没在跑）"%TARGET)
h=k32.OpenProcess(0x0400|0x0010,False,pid)
def u32(a):
    b=ctypes.create_string_buffer(4); got=ctypes.c_size_t()
    if not k32.ReadProcessMemory(h,ctypes.c_void_p(a&0xFFFFFFFF),ctypes.byref(b),4,ctypes.byref(got)) or got.value!=4: return None
    return struct.unpack('<I',b.raw)[0]
def region_ok(a,n):
    mbi=MBI()
    if not k32.VirtualQueryEx(h,ctypes.c_void_p(a),ctypes.byref(mbi),ctypes.sizeof(mbi)): return False
    if mbi.State!=0x1000 or (mbi.Protect & 0x101)!=0: return False
    base=mbi.BaseAddress or 0
    return a>=base and a+n<=base+mbi.RegionSize
def read_span(a,n):
    out=b''
    cur=a
    while len(out)<n:
        chunk=min(0x1000, n-len(out))
        if not region_ok(cur,chunk): break
        b=ctypes.create_string_buffer(chunk); got=ctypes.c_size_t()
        if not k32.ReadProcessMemory(h,ctypes.c_void_p(cur),ctypes.byref(b),chunk,ctypes.byref(got)) or got.value!=chunk: break
        out+=b.raw; cur+=chunk
    return out
root=u32(BASE+ROOT_RVA); node=u32(root+0x50); wrap=u32(node+8); obj=u32(wrap+0x138)
owner=u32(obj+0x418); entry=u32(obj+0x414)
print("PID=%d 对象 0x%08X owner=0x%08X 条目=0x%08X" % (pid, obj, owner or 0, entry or 0))
glob=u32(PLAYER_LIST); players=[]
for i in range(10):
    p=u32(glob+TABLE_OFF+i*4) if glob else None
    if not p: continue
    e=u32(p+P_ENTRY)
    players.append((i,p,e))
    mark = "← 我(本对象 owner)" if p==owner else ("← 本对象条目是这个玩家的" if e==entry else "")
    print("   玩家槽%d obj=0x%08X [+0xAC]=0x%08X %s" % (i,p,e or 0,mark))
old = [p for i,p,e in players if e and e!=entry]
span = read_span(obj, 0x2000)
print("\n对象体内（0x%X 字节）里指向各玩家的字段：" % len(span))
for i,p,e in players:
    hits=[off for off in range(0,len(span)-4,4) if struct.unpack('<I',span[off:off+4])[0]==p]
    if hits: print("   槽%d 玩家 0x%08X: %s" % (i,p," ".join("+0x%X"%o for o in hits[:12])))
e_hits=[off for off in range(0,len(span)-4,4) if struct.unpack('<I',span[off:off+4])[0]==entry]
print("   本对象条目值 0x%08X 出现在: %s" % (entry or 0, " ".join("+0x%X"%o for o in e_hits[:12]) or "无"))
for i,p,e in players:
    if not e or e==entry: continue
    h2=[off for off in range(0,len(span)-4,4) if struct.unpack('<I',span[off:off+4])[0]==e]
    if h2: print("   ⚠ 旧主条目 0x%08X（槽%d）残留在: %s" % (e,i," ".join("+0x%X"%o for o in h2[:12])))
