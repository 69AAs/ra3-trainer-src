// Executable synthetic x86 fixtures; no GPU or driver required.
#define CODE_HOOK_TESTING
#define wmain HarnessMain
#include "harness.cpp"
#undef wmain
using Fn=int(__cdecl*)();
static int Replacement() { return 99; }
static int Replacement2() { return 77; }
static LONG heldRefs=1;
static ULONG WINAPI Hold(IDirect3DDevice9*) { return ++heldRefs; }
static ULONG WINAPI Drop(IDirect3DDevice9*) { return --heldRefs; }
static HRESULT WINAPI Creation(IDirect3DDevice9*,D3DDEVICE_CREATION_PARAMETERS* p) { *p={}; return S_OK; }
static void Put(BYTE* code,const BYTE* bytes,size_t count) {
    DWORD old,discard;
    if (!VirtualProtect(code,64,PAGE_READWRITE,&old)) ExitProcess(2);
    memset(code,0x90,64); memcpy(code,bytes,count);
    if (!VirtualProtect(code,64,PAGE_EXECUTE_READ,&discard) ||
        !FlushInstructionCache(GetCurrentProcess(),code,64)) ExitProcess(2);
}
static DWORD Protection(void* p) { MEMORY_BASIC_INFORMATION m={}; VirtualQuery(p,&m,sizeof(m)); return m.Protect; }
int wmain() {
    setvbuf(stdout,nullptr,_IONBF,0);
    BYTE* code=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!code) return 2;
    const BYTE constant[]={0xb8,42,0,0,0,0xc3};
    Put(code,constant,sizeof(constant));
    CodeHook h;
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)),"prepare decoded mov imm32");
    Check(h.length==5 && Protection(h.trampoline)==PAGE_EXECUTE_READ,"whole instruction and RX trampoline");
    Check(reinterpret_cast<Fn>(h.trampoline)()==42,"trampoline executes original semantics");
    Check(h.Install() && reinterpret_cast<Fn>(code)()==99,"actual function body redirects");
    Check(Protection(code)==PAGE_EXECUTE_READ,"install restores code page protection");
    Check(!h.Install(),"duplicate install rejected");
    Check(h.Restore() && !memcmp(code,constant,sizeof(constant)),"uninstall restores every original byte");
    Check(reinterpret_cast<Fn>(code)()==42 && Protection(code)==PAGE_EXECUTE_READ,"restored function executes and protection equal");
    h.Release();

    const BYTE prologue[]={0x8b,0xff,0x55,0x8b,0xec,0xb8,42,0,0,0,0x5d,0xc3};
    Put(code,prologue,sizeof(prologue));
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)) && h.length==5,"common hotpatch frame prologue decoded");
    Check(reinterpret_cast<Fn>(h.trampoline)()==42,"frame setup stack semantics preserved");
    h.Release();
    const BYTE sib[]={0x8b,0x44,0x24,0x04,0x90,0xc3};
    Put(code,sib,sizeof(sib));
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)) && h.length==5,"ModRM SIB displacement boundary preserved");
    h.Release();

    Put(code+128,constant,sizeof(constant));
    BYTE branch[6]={0xe9,0,0,0,0,0xc3};
    CodeHook::Jump(branch,reinterpret_cast<uintptr_t>(code),reinterpret_cast<uintptr_t>(code+128));
    Put(code,branch,sizeof(branch));
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)),"rel32 entry branch accepted");
    Check(reinterpret_cast<Fn>(h.trampoline)()==42,"rel32 branch destination relocated correctly");
    Check(h.Install() && h.Restore() && !memcmp(code,branch,sizeof(branch)),"entry branch restored byte for byte");
    h.Release();

    const BYTE rejected[][8]={
        {0xe8,0,0,0,0,0x58,0xc3}, // call/pop EIP is deliberately unsupported
        {0xeb,3,0x90,0x90,0x90,0xc3},
        {0x74,3,0x90,0x90,0x90,0xc3},
        {0xe2,3,0x90,0x90,0x90,0xc3},
        {0x66,0xb8,0,0,0x90,0xc3},
        {0xff,0xd0,0x90,0x90,0x90,0xc3},
        {0xd9,0x74,0x24,0xf4,0x58,0xc3}, // FNSTENV can expose original EIP
        {0xc3,0x90,0x90,0x90,0x90},
        {0xe9,0xfb,0xff,0xff,0xff}, // branch into overwritten entry
        {0x0f,0x85,0,0,0,0,0xc3},
        {0xf0,0x90,0x90,0x90,0x90}
    };
    for (const auto& bytes:rejected) {
        Put(code,bytes,sizeof(bytes));
        Check(!h.Prepare(code,reinterpret_cast<void*>(&Replacement)) && !memcmp(code,bytes,sizeof(bytes)),
            "unsupported control flow/prefix rejects without writing code");
    }
    Check(!h.Prepare(nullptr,reinterpret_cast<void*>(&Replacement)),"null target rejected");
    Check(!h.Prepare(code,nullptr),"null replacement rejected");
    BYTE data[32]={}; Check(!h.Prepare(data,reinterpret_cast<void*>(&Replacement)),"nonexecutable source rejected");
    Put(code,constant,sizeof(constant));
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)),"prepare foreign modification case");
    BYTE foreign[6]={0xb8,7,0,0,0,0xc3}; Put(code,foreign,sizeof(foreign));
    Check(!h.Install() && !memcmp(code,foreign,sizeof(foreign)),"changed original refuses publication");
    h.Release(); Put(code,constant,sizeof(constant));
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)) && h.Install(),"install foreign ownership case");
    Put(code,foreign,sizeof(foreign));
    Check(!h.Restore() && h.installed && !memcmp(code,foreign,sizeof(foreign)),"foreign code not overwritten and trampoline retained");
    Put(code,h.patch,h.length);
    Check(h.Restore(),"ownership re-established permits exact restore"); h.Release();

    // Two layers patch the SAME original address, as capture + actual DLL do.
    Put(code,constant,sizeof(constant)); CodeHook inner,outer;
    Check(inner.Prepare(code,reinterpret_cast<void*>(&Replacement)) && inner.Install(),"inner layer installed");
    Check(outer.Prepare(code,reinterpret_cast<void*>(&Replacement2)) && outer.Install() && reinterpret_cast<Fn>(code)()==77,"outer relocates inner jump");
    Check(reinterpret_cast<Fn>(outer.trampoline)()==99,"outer trampoline delegates inner replacement");
    Check(outer.Restore() && inner.Equals(inner.patch),"outer detach restores inner exact patch"); outer.Release();
    Check(inner.Restore() && !memcmp(code,constant,sizeof(constant)),"inner detach restores original driver bytes"); inner.Release();

    for (int stage=1;stage<=3;stage++) {
        Put(code,constant,sizeof(constant));
        Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)),"failure injection fixture prepared");
        if (stage<3) CodeHook::failProtectAt=stage; else CodeHook::failFlushAt=1;
        Check(!h.Install(),"install VirtualProtect/flush failure reported");
        Check(h.Restore() && h.Equals(h.before) && Protection(code)==PAGE_EXECUTE_READ,
            "failed install rolls back exact bytes and original protection"); h.Release();
    }
    Put(code,constant,sizeof(constant));
    Check(h.Prepare(code,reinterpret_cast<void*>(&Replacement)) && h.Install(),"restore failure fixture installed");
    CodeHook::failProtectAt=1;
    Check(!h.Restore() && h.installed && h.trampoline && h.Equals(h.patch),"restore protection failure retains executable trampoline");
    Check(h.Restore() && h.Equals(h.before),"subsequent proven safe boundary restores bytes"); h.Release();

    // A read-only, oversized Ex-like device catches even a single vptr write.
    BYTE* object=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    void** table=reinterpret_cast<void**>(object+1024);
    memset(object,0x5a,1024); *reinterpret_cast<void***>(object)=table;
    for (int i=0;i<160;i++) table[i]=code;
    BYTE snapshot[4096]; memcpy(snapshot,object,sizeof(snapshot)); DWORD old;
    if (!VirtualProtect(object,4096,PAGE_READONLY,&old)) return 2;
    Check(InstallCapture(reinterpret_cast<IDirect3DDevice9*>(object)),"actual capture installs against read-only oversized object/table");
    Check(!memcmp(snapshot,object,sizeof(snapshot)),"all 4096 device/table bytes unchanged including slots beyond 134");
    Check(captureHook.Restore() && !memcmp(snapshot,object,sizeof(snapshot)),"detach leaves every device/table byte unchanged"); captureHook.Release();
    // Verify the ACTUAL DLL also respects the read-only device and table.
    VirtualProtect(object,4096,PAGE_READWRITE,&old);
    table[1]=reinterpret_cast<void*>(&Hold); table[2]=reinterpret_cast<void*>(&Drop);
    table[9]=reinterpret_cast<void*>(&Creation); table[16]=code+128;
    const BYTE presentBody[]={0xb8,0x68,0x08,0x76,0x88,0xc2,20,0}; // DEVICELOST
    const BYTE resetBody[]={0xb8,0,0,0,0,0xc2,8,0};
    Put(code,presentBody,sizeof(presentBody)); Put(code+128,resetBody,sizeof(resetBody));
    memcpy(snapshot,object,sizeof(snapshot)); VirtualProtect(object,4096,PAGE_READONLY,&old);
    HMODULE dll=LoadLibraryW(L"Ra3Overlay.dll"); if (!dll) return 2;
    auto begin=reinterpret_cast<StartOverlay>(GetProcAddress(dll,"OverlayStart"));
    auto end=reinterpret_cast<StopOverlay>(GetProcAddress(dll,"OverlayStop"));
    auto read=reinterpret_cast<ReadOverlay>(GetProcAddress(dll,"OverlayRead"));
    if (!begin || !end || !read) return 2;
    auto readonlyDevice=reinterpret_cast<IDirect3DDevice9*>(object);
    Check(begin(readonlyDevice) && heldRefs==2,"actual DLL installs on entirely read-only device");
    Check(!memcmp(snapshot,object,sizeof(snapshot)),"actual DLL installation does not write object/table");
    struct Alias { void** vt; } alias={table}; auto other=reinterpret_cast<IDirect3DDevice9*>(&alias);
    D3DPRESENT_PARAMETERS pp={}; OverlayStats os={};
    Check(other->Reset(&pp)==S_OK && other->Present(nullptr,nullptr,nullptr,nullptr)==D3DERR_DEVICELOST,
        "other devices sharing code preserve original HRESULTs");
    read(&os);
    Check(os.enabled && !os.resets && !os.frames,"other devices cannot disable or reset owner overlay");
    Check(!end(FALSE),"actual DLL requires host quiescence proof");
    Check(end(TRUE) && heldRefs==1 && !memcmp(snapshot,object,sizeof(snapshot)),"actual DLL detach keeps all device bytes unchanged");
    Check(!memcmp(code,presentBody,sizeof(presentBody)) && !memcmp(code+128,resetBody,sizeof(resetBody)),
        "actual DLL restores both function bodies byte for byte");
    FreeLibrary(dll);
    VirtualFree(object,0,MEM_RELEASE);
    // Truncated instruction at the end of readable executable memory.
    BYTE* boundary=static_cast<BYTE*>(VirtualAlloc(nullptr,8192,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    boundary[4095]=0xb8;
    VirtualProtect(boundary,4096,PAGE_EXECUTE_READ,&old); VirtualProtect(boundary+4096,4096,PAGE_NOACCESS,&old);
    Check(!h.Prepare(boundary+4095,reinterpret_cast<void*>(&Replacement)),"decoder never reads across inaccessible region");
    VirtualFree(boundary,0,MEM_RELEASE); VirtualFree(code,0,MEM_RELEASE);
    printf("RESULT code-hook checks=%d failures=%d\n",checks,failures);
    return failures?1:0;
}
