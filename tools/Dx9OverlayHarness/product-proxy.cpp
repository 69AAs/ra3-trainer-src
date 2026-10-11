#include "overlay.h"
#include <new>
#include <stdio.h>
#include <string.h>
#define PRODUCT_PROXY_ONLY
#include "product-status.h"
#include "entry-verification.h"
#include "thread-policy.h"
extern "C" BOOL __cdecl OverlayStart(IDirect3DDevice9*) noexcept;
// Only the factory is a wrapper. The device returned to the game is the exact
// pointer returned by system D3D9; no device object/vtable is copied or changed.
static INIT_ONCE init=INIT_ONCE_STATIC_INIT;
static HMODULE systemD3D;
// Pin the exact file before LoadLibrary; deny writes/deletion through process
// lifetime, matching the resident system DLL. Do not reopen a replaceable path.
static HANDLE systemImageFile=INVALID_HANDLE_VALUE;
static EntryVerification ProductVerifyEntry(void* address,BYTE* live) {
    EntryVerification result; HMODULE module=nullptr;
    if(!address || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(address),&module)) return result;
    if(!ReadEntryBytes(address,live,32)) { FreeLibrary(module); return result; }
    if(module!=systemD3D) {
        result.reason=strcmp(DecodeEntryTransfer(live,32,reinterpret_cast<uintptr_t>(address)).kind,"none")?"entry-branch":"image-mismatch";
        FreeLibrary(module); return result;
    }
    HANDLE f=systemImageFile;
    if(f!=INVALID_HANDLE_VALUE) {
        LARGE_INTEGER length={};
        if(GetFileSizeEx(f,&length) && length.QuadPart>0 && length.QuadPart<=256*1024*1024) {
            HANDLE mapping=CreateFileMappingW(f,nullptr,PAGE_READONLY,0,0,nullptr);
            const BYTE* bytes=mapping?static_cast<const BYTE*>(MapViewOfFile(mapping,FILE_MAP_READ,0,0,0)):nullptr;
            if(bytes) {
                EntryImage image;
                if(image.Parse(bytes,static_cast<size_t>(length.QuadPart))) {
                    // Full immutable header snapshot: DOS/NT identity, timestamp,
                    // preferred base, sizes, checksum, directory and section layout.
                    DWORD headers=image.nt.OptionalHeader.SizeOfHeaders;
                    BYTE* loaded=headers<=1024*1024?new(std::nothrow) BYTE[headers]:nullptr;
                    bool identity=loaded && ReadEntryBytes(module,loaded,headers) && image.MatchesHeaders(loaded,static_cast<DWORD>(reinterpret_cast<uintptr_t>(module)));
                    delete[] loaded;
                    DWORD rva=static_cast<DWORD>(reinterpret_cast<uintptr_t>(address)-reinterpret_cast<uintptr_t>(module));
                    result=VerifyImageEntry(image,rva,static_cast<DWORD>(reinterpret_cast<uintptr_t>(module)),live,identity);
                }
                UnmapViewOfFile(bytes);
            }
            if(mapping) CloseHandle(mapping);
        }
    }
    FreeLibrary(module); return result;
}
static bool ProductCompareEntry(void* address,BYTE* observedDisk=nullptr,bool* diskRead=nullptr,BYTE* observedLive=nullptr) {
    BYTE live[32]={}; auto result=ProductVerifyEntry(address,live);
    if(observedLive) memcpy(observedLive,live,32);
    if(observedDisk && result.diskRead) memcpy(observedDisk,result.disk,32);
    if(diskRead) *diskRead=result.diskRead;
    return strcmp(result.reason,"verified-system")==0;
}
bool ProductEntryOwned(void* address) { return ProductCompareEntry(address); }
static void ProductFormatEntry(const char* slot,void* address,char* line,size_t capacity) {
    HMODULE module=nullptr;
    WCHAR path[MAX_PATH]={}; char name[MAX_PATH*3]={};
    // Hold a temporary reference while resolving the name; UNCHANGED_REFCOUNT
    // could attribute a recycled handle to the wrong image during concurrent unload.
    bool found=address && GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
        reinterpret_cast<LPCWSTR>(address),&module)!=FALSE;
    if (found && GetModuleFileNameW(module,path,MAX_PATH)<MAX_PATH) {
        const WCHAR* base=wcsrchr(path,L'\\'); base=base?base+1:path;
        WideCharToMultiByte(CP_UTF8,0,base,-1,name,sizeof(name),nullptr,nullptr);
    }
    if (!name[0]) strcpy(name,"unidentified");
    for (char* c=name;*c;++c) if (*c=='\r' || *c=='\n' || *c=='|') *c='_';
    BYTE live[32]={},disk[32]={}; bool diskRead=false;
    bool readable=ReadEntryBytes(address,live,sizeof(live));
    auto verification=ProductVerifyEntry(address,live); diskRead=verification.diskRead; if(diskRead) memcpy(disk,verification.disk,32);
    if(diskRead) readable=true;
    auto transfer=DecodeEntryTransfer(live,readable?sizeof(live):0,reinterpret_cast<uintptr_t>(address));
    if(transfer.cell) { uint32_t v=0; transfer.resolved=ReadEntryBytes(reinterpret_cast<void*>(transfer.cell),&v,4); if(transfer.resolved) transfer.target=v; }
    char targetModule[MAX_PATH*3]; EntryModuleName(transfer.resolved?reinterpret_cast<void*>(transfer.target):nullptr,targetModule,sizeof(targetModule));
    char raw[65]="unreadable",file[65]="unavailable";
    if(readable) EntryHex(live,32,raw);
    if(diskRead) EntryHex(disk,32,file);
    const char* reason=verification.reason;
    // A target identifies the destination, NOT the author of a patch.
    const char* shape=readable && live[0]==0x8b && live[1]==0xff?"hotpatch-prefix":"ordinary-or-unknown";
    char bytes[33]="unreadable";
    if(readable) EntryHex(live,16,bytes);
    char expected[65]="unavailable";
    if(verification.normalized) EntryHex(verification.expected,32,expected);
    // Keep the historical membership field for old log consumers/tests. It is
    // NOT the verdict and does not identify a writer. reason has exactly 5 values.
    const char* legacy=found && module!=systemD3D?"foreign-module":"not-applicable";
    snprintf(line,capacity,"osd-entry slot=%s bytes=%s target=0x%08lX targetModule=%s reason=%s address=%p raw=%s disk=%s transfer=%s cell=%p resolved=%u shape=%s attribution=destination-not-writer expected=%s delta=0x%08lX relocations=%u legacy_reason=%s module=%s",
        slot,bytes,static_cast<unsigned long>(transfer.target),targetModule,reason,address,raw,file,transfer.kind,
        reinterpret_cast<void*>(transfer.cell),transfer.resolved?1u:0u,shape,expected,verification.delta,verification.relocations,legacy,name);
    if (found) FreeLibrary(module);
}
void ProductDescribeEntry(const char* slot,void* address) {
    char line[4096]; ProductFormatEntry(slot,address,line,sizeof(line));
    ProductLog(line);
}
static BOOL CALLBACK LoadSystem(PINIT_ONCE,void*,void**) {
    WCHAR path[MAX_PATH]; UINT n=GetSystemDirectoryW(path,MAX_PATH);
    if (!n || n+11>=MAX_PATH) return FALSE;
    wcscat(path,L"\\d3d9.dll");
    systemImageFile=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr);
    // Verification failures may refuse OSD but must not break system forwarding.
    systemD3D=LoadLibraryW(path);
    if(!systemD3D && systemImageFile!=INVALID_HANDLE_VALUE) { CloseHandle(systemImageFile); systemImageFile=INVALID_HANDLE_VALUE; }
    if (systemD3D) {
        FILETIME created,exit,kernel,user;
        if (GetProcessTimes(GetCurrentProcess(),&created,&exit,&kernel,&user)) {
            char session[80]; snprintf(session,sizeof(session),"osd-session %08lX%08lX",created.dwHighDateTime,created.dwLowDateTime);
            ProductLog(session);
        }
        ProductLog("osd-proxy-loaded system-forwarder");
    }
    return systemD3D!=nullptr;
}
static FARPROC Real(const char* name) {
    if (!InitOnceExecuteOnce(&init,LoadSystem,nullptr,nullptr)) return nullptr;
    return GetProcAddress(systemD3D,name);
}
static LONG captureAttempt;
static void Capture(IDirect3DDevice9* d) {
    if (!d || InterlockedCompareExchange(&captureAttempt,1,0)) return;
    ProductLog("osd-device-captured CreateDevice");
    if (!OverlayStart(d)) ProductLog("osd-install-refused retain-disabled");
}
class Factory final : public IDirect3D9Ex {
    IDirect3D9* real;
    IDirect3D9Ex* ex;
    LONG refs=1;
public:
    explicit Factory(IDirect3D9* r,IDirect3D9Ex* e):real(r),ex(e) {}
    HRESULT WINAPI QueryInterface(REFIID id,void** out) override {
        if (!out) return E_POINTER;
        *out=nullptr;
        if (id==IID_IUnknown || id==IID_IDirect3D9 || (ex && id==IID_IDirect3D9Ex)) {
            *out=static_cast<IDirect3D9Ex*>(this); AddRef(); return S_OK;
        }
        return E_NOINTERFACE;
    }
    ULONG WINAPI AddRef() override { return InterlockedIncrement(&refs); }
    ULONG WINAPI Release() override {
        LONG n=InterlockedDecrement(&refs);
        if (!n) { real->Release(); delete this; }
        return n;
    }
    HRESULT WINAPI RegisterSoftwareDevice(void* a) override { return real->RegisterSoftwareDevice(a); }
    UINT WINAPI GetAdapterCount() override { return real->GetAdapterCount(); }
    HRESULT WINAPI GetAdapterIdentifier(UINT a,DWORD b,D3DADAPTER_IDENTIFIER9* c) override { return real->GetAdapterIdentifier(a,b,c); }
    UINT WINAPI GetAdapterModeCount(UINT a,D3DFORMAT b) override { return real->GetAdapterModeCount(a,b); }
    HRESULT WINAPI EnumAdapterModes(UINT a,D3DFORMAT b,UINT c,D3DDISPLAYMODE* d) override { return real->EnumAdapterModes(a,b,c,d); }
    HRESULT WINAPI GetAdapterDisplayMode(UINT a,D3DDISPLAYMODE* b) override { return real->GetAdapterDisplayMode(a,b); }
    HRESULT WINAPI CheckDeviceType(UINT a,D3DDEVTYPE b,D3DFORMAT c,D3DFORMAT d,BOOL e) override { return real->CheckDeviceType(a,b,c,d,e); }
    HRESULT WINAPI CheckDeviceFormat(UINT a,D3DDEVTYPE b,D3DFORMAT c,DWORD d,D3DRESOURCETYPE e,D3DFORMAT f) override { return real->CheckDeviceFormat(a,b,c,d,e,f); }
    HRESULT WINAPI CheckDeviceMultiSampleType(UINT a,D3DDEVTYPE b,D3DFORMAT c,BOOL d,D3DMULTISAMPLE_TYPE e,DWORD* f) override { return real->CheckDeviceMultiSampleType(a,b,c,d,e,f); }
    HRESULT WINAPI CheckDepthStencilMatch(UINT a,D3DDEVTYPE b,D3DFORMAT c,D3DFORMAT d,D3DFORMAT e) override { return real->CheckDepthStencilMatch(a,b,c,d,e); }
    HRESULT WINAPI CheckDeviceFormatConversion(UINT a,D3DDEVTYPE b,D3DFORMAT c,D3DFORMAT d) override { return real->CheckDeviceFormatConversion(a,b,c,d); }
    HRESULT WINAPI GetDeviceCaps(UINT a,D3DDEVTYPE b,D3DCAPS9* c) override { return real->GetDeviceCaps(a,b,c); }
    HMONITOR WINAPI GetAdapterMonitor(UINT a) override { return real->GetAdapterMonitor(a); }
    HRESULT WINAPI CreateDevice(UINT a,D3DDEVTYPE b,HWND c,DWORD d,D3DPRESENT_PARAMETERS* e,IDirect3DDevice9** f) override {
        const DWORD tid=GetCurrentThreadId();
        HRESULT hr=real->CreateDevice(a,b,c,d,e,f);
        const OverlayCreationCall call{SUCCEEDED(hr) && f?*f:nullptr,tid,d,b,c,e?e->hDeviceWindow:nullptr,hr};
        char line[320];
        snprintf(line,sizeof(line),"osd-draw-thread role=CreateDevice-call tid=%lu flags=0x%08lx hr=%08lx DeviceType=%u hFocusWindow=%p hDeviceWindow=%p device=%p source=CreateDevice-entry flagsSource=call-argument",
            tid,d,static_cast<unsigned long>(hr),static_cast<unsigned>(b),c,call.deviceWindow,call.device);
        ProductLog(line);
        const auto previous=overlayCreationCall;
        overlayCreationCall=&call;
        if (SUCCEEDED(hr) && f) Capture(*f);
        overlayCreationCall=previous;
        return hr;
    }
    UINT WINAPI GetAdapterModeCountEx(UINT a,const D3DDISPLAYMODEFILTER* b) override { return ex?ex->GetAdapterModeCountEx(a,b):0; }
    HRESULT WINAPI EnumAdapterModesEx(UINT a,const D3DDISPLAYMODEFILTER* b,UINT c,D3DDISPLAYMODEEX* d) override { return ex?ex->EnumAdapterModesEx(a,b,c,d):D3DERR_NOTAVAILABLE; }
    HRESULT WINAPI GetAdapterDisplayModeEx(UINT a,D3DDISPLAYMODEEX* b,D3DDISPLAYROTATION* c) override { return ex?ex->GetAdapterDisplayModeEx(a,b,c):D3DERR_NOTAVAILABLE; }
    HRESULT WINAPI CreateDeviceEx(UINT a,D3DDEVTYPE b,HWND c,DWORD d,D3DPRESENT_PARAMETERS* e,D3DDISPLAYMODEEX* f,IDirect3DDevice9Ex** g) override {
        if (!ex) return D3DERR_NOTAVAILABLE;
        HRESULT hr=ex->CreateDeviceEx(a,b,c,d,e,f,g);
        if (SUCCEEDED(hr) && g && *g) {
            // PresentEx/ResetEx are not covered by the proven 9 renderer.
            ProductLog("osd-device-captured CreateDeviceEx");
            ProductLog("osd-install-refused Ex-presentation");
        }
        return hr;
    }
    HRESULT WINAPI GetAdapterLUID(UINT a,LUID* b) override { return ex?ex->GetAdapterLUID(a,b):D3DERR_NOTAVAILABLE; }
};
extern "C" __declspec(dllexport) IDirect3D9* WINAPI Direct3DCreate9(UINT sdk) {
    auto f=reinterpret_cast<IDirect3D9*(WINAPI*)(UINT)>(Real("Direct3DCreate9"));
    IDirect3D9* d=f?f(sdk):nullptr;
    if (!d) return nullptr;
    auto wrapper=new(std::nothrow) Factory(d,nullptr);
    if (!wrapper) { ProductLog("osd-install-refused allocation"); return d; }
    return wrapper;
}
extern "C" __declspec(dllexport) HRESULT WINAPI Direct3DCreate9Ex(UINT sdk,IDirect3D9Ex** out) {
    if (!out) return E_POINTER;
    *out=nullptr;
    auto f=reinterpret_cast<HRESULT(WINAPI*)(UINT,IDirect3D9Ex**)>(Real("Direct3DCreate9Ex"));
    if (!f) return D3DERR_NOTAVAILABLE;
    HRESULT hr=f(sdk,out);
    if (SUCCEEDED(hr) && *out) { auto w=new(std::nothrow) Factory(*out,*out); if (w) *out=w; }
    return hr;
}
// Forward the documented non-factory D3D9 exports with exact calling conventions.
extern "C" __declspec(dllexport) int WINAPI D3DPERF_BeginEvent(D3DCOLOR c,LPCWSTR n) { auto f=reinterpret_cast<int(WINAPI*)(D3DCOLOR,LPCWSTR)>(Real("D3DPERF_BeginEvent")); return f?f(c,n):-1; }
extern "C" __declspec(dllexport) int WINAPI D3DPERF_EndEvent() { auto f=reinterpret_cast<int(WINAPI*)()>(Real("D3DPERF_EndEvent")); return f?f():-1; }
extern "C" __declspec(dllexport) DWORD WINAPI D3DPERF_GetStatus() { auto f=reinterpret_cast<DWORD(WINAPI*)()>(Real("D3DPERF_GetStatus")); return f?f():0; }
extern "C" __declspec(dllexport) BOOL WINAPI D3DPERF_QueryRepeatFrame() { auto f=reinterpret_cast<BOOL(WINAPI*)()>(Real("D3DPERF_QueryRepeatFrame")); return f?f():FALSE; }
extern "C" __declspec(dllexport) void WINAPI D3DPERF_SetMarker(D3DCOLOR c,LPCWSTR n) { auto f=reinterpret_cast<void(WINAPI*)(D3DCOLOR,LPCWSTR)>(Real("D3DPERF_SetMarker")); if(f) f(c,n); }
extern "C" __declspec(dllexport) void WINAPI D3DPERF_SetRegion(D3DCOLOR c,LPCWSTR n) { auto f=reinterpret_cast<void(WINAPI*)(D3DCOLOR,LPCWSTR)>(Real("D3DPERF_SetRegion")); if(f) f(c,n); }
extern "C" __declspec(dllexport) void WINAPI D3DPERF_SetOptions(DWORD a) { auto f=reinterpret_cast<void(WINAPI*)(DWORD)>(Real("D3DPERF_SetOptions")); if(f) f(a); }
