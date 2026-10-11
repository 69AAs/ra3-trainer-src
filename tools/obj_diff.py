
"""对象字段差分（**只读**）：把"当前选中的对象"与"给定地址的对象"逐 dword 对比。
用法：python tools/obj_diff.py --vs 068458B0 [--span 0x1000]
"""
import ctypes, ctypes.wintypes as wt, struct, sys
TARGET="ra3_1.12.game"
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
if pid is None: raise SystemExit("找不到 %s"%TARGET)
h=k32.OpenProcess(0x0400|0x0010,False,pid)
def u32(a):
    b=ctypes.create_string_buffer(4); got=ctypes.c_size_t()
    if not k32.ReadProcessMemory(h,ctypes.c_void_p(a&0xFFFFFFFF),ctypes.byref(b),4,ctypes.byref(got)) or got.value!=4: return None
    return struct.unpack('<I',b.raw)[0]
def span(a,n):
    out=b''; cur=a
    while len(out)<n:
        chunk=min(0x1000,n-len(out)); mbi=MBI()
        if not k32.VirtualQueryEx(h,ctypes.c_void_p(cur),ctypes.byref(mbi),ctypes.sizeof(mbi)): break
        if mbi.State!=0x1000 or (mbi.Protect&0x101)!=0: break
        base=mbi.BaseAddress or 0
        if not (base<=cur and cur+chunk<=base+mbi.RegionSize): break
        b=ctypes.create_string_buffer(chunk); got=ctypes.c_size_t()
        if not k32.ReadProcessMemory(h,ctypes.c_void_p(cur),ctypes.byref(b),chunk,ctypes.byref(got)) or got.value!=chunk: break
        out+=b.raw; cur+=chunk
    return out
root=u32(0x400000+0x8DB73C); node=u32(root+0x50) if root else None
wrap=u32(node+8) if node else None; A=u32(wrap+0x138) if wrap else None
B=None; N=0x1000
for i,a in enumerate(sys.argv):
    if a=="--vs" and i+1<len(sys.argv): B=int(sys.argv[i+1],16)
    if a=="--span" and i+1<len(sys.argv): N=int(sys.argv[i+1],0)
if not A: raise SystemExit("先选中一个对象")
if not B: raise SystemExit("给 --vs <地址>")
def info(x,label):
    tt=u32(x+4); ty=u32(tt+8) if tt else None
    print("%s 0x%08X type=0x%08X owner=0x%08X 条目=0x%08X" % (label, x, ty or 0, u32(x+0x418) or 0, u32(x+0x414) or 0))
info(A,"选中  "); info(B,"对照  ")
if u32(A+4) and u32(B+4) and u32(u32(A+4)+8)==u32(u32(B+4)+8):
    print("★ 两栋**同型号**（type 相同）⇒ 下面的差值可以直读")
else:
    print("⚠ 两栋型号不同 ⇒ 差值里混有型号差异，只看结构性差异（指针有无）")
sa, sb = span(A,N), span(B,N)
n=min(len(sa),len(sb)); rows=[]
for off in range(0,n-4,4):
    a=struct.unpack('<I',sa[off:off+4])[0]; b=struct.unpack('<I',sb[off:off+4])[0]
    if a==b: continue
    pa=0x02000000<=a<=0x20000000; pb=0x02000000<=b<=0x20000000
    kind = "指针有无" if (pa!=pb) else ("指针不同" if (pa and pb) else "数值不同")
    rows.append((off,a,b,kind))
print("\n差值 %d 处（0x000..0x%X）" % (len(rows), N))
for off,a,b,kind in rows[:40]:
    print("   +0x%04X  选中=0x%08X  对照=0x%08X  [%s]" % (off,a,b,kind))
