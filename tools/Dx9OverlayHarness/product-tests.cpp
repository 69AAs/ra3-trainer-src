#include "product-status.h"
#include "product-proxy.cpp"
#include "code-hook.h"
#include "product-freeze.h"
static int checks, failures, starts, releases;
static IDirect3DDevice9* captured;
extern "C" BOOL __cdecl OverlayStart(IDirect3DDevice9* d) noexcept { ++starts; captured=d; return TRUE; }
static void Check(bool ok,const char* text) { ++checks; if (!ok) ++failures; printf("%s %s\n",ok?"PASS":"FAIL",text); }
static ULONG WINAPI FakeRelease(IDirect3D9*) { ++releases; return 0; }
static UINT WINAPI FakeAdapters(IDirect3D9*) { return 4; }
static HRESULT createResult=S_OK;
static IDirect3DDevice9* delivered;
static HRESULT WINAPI FakeCreate(IDirect3D9*,UINT,D3DDEVTYPE,HWND,DWORD,D3DPRESENT_PARAMETERS*,IDirect3DDevice9** out) {
    *out=SUCCEEDED(createResult)?delivered:nullptr; return createResult;
}
int wmain() {
    void* table[22]={}; table[2]=reinterpret_cast<void*>(&FakeRelease); table[4]=reinterpret_cast<void*>(&FakeAdapters); table[16]=reinterpret_cast<void*>(&FakeCreate);
    struct { void** vtable; BYTE reserved[64]; } object={table,{}};
    auto original=reinterpret_cast<IDirect3D9*>(&object);
    BYTE* page=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!page) return 2;
    memset(page,0x5a,4096); DWORD old; VirtualProtect(page,4096,PAGE_READONLY,&old);
    delivered=reinterpret_cast<IDirect3DDevice9*>(page);
    auto f=new Factory(original,nullptr);
    Check(f->GetAdapterCount()==4,"factory forwards adapter result");
    void* identity=nullptr;
    Check(f->QueryInterface(IID_IDirect3D9,&identity)==S_OK && identity==f,"factory public COM identity");
    Check(f->Release()==1,"factory balanced QI reference");
    Check(f->QueryInterface(IID_IDirect3D9Ex,&identity)==E_NOINTERFACE && identity==nullptr,"non-Ex does not fabricate Ex support");
    IDirect3DDevice9* device=nullptr;
    createResult=D3DERR_NOTAVAILABLE;
    Check(f->CreateDevice(0,D3DDEVTYPE_HAL,nullptr,0,nullptr,&device)==createResult && starts==0,"failed CreateDevice preserved and not captured");
    createResult=S_OK;
    Check(f->CreateDevice(0,D3DDEVTYPE_HAL,nullptr,0,nullptr,&device)==S_OK && starts==1 && captured==device && device==delivered,"captures actual returned device before publication");
    bool intact=true; for (int i=0;i<4096;++i) if (page[i]!=0x5a) intact=false;
    Check(intact,"read-only device page unchanged including vptr");
    f->CreateDevice(0,D3DDEVTYPE_HAL,nullptr,0,nullptr,&device);
    Check(starts==1,"later devices never repatch shared entry");
    Check(f->Release()==0 && releases==1,"factory owns and releases original reference");
    VirtualFree(page,0,MEM_RELEASE);

    FILETIME created,exit,kernel,user; GetProcessTimes(GetCurrentProcess(),&created,&exit,&kernel,&user);
    ULONGLONG creationIdentity=(static_cast<ULONGLONG>(created.dwHighDateTime)<<32)|created.dwLowDateTime;
    WCHAR name[96]; swprintf(name,96,L"Local\\Ra3Trainer.Overlay.v1.%lu.%016llX",GetCurrentProcessId(),creationIdentity);
    HANDLE mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(ProductStatus),name);
    auto writer=static_cast<ProductStatus*>(MapViewOfFile(mapping,FILE_MAP_WRITE,0,0,sizeof(ProductStatus)));
    if (!writer) return 2;
    *writer={}; writer->magic=0x3544534f; writer->size=sizeof(ProductStatus); writer->sequence=2;
    writer->targetPid=GetCurrentProcessId(); writer->heartbeat=GetTickCount(); writer->enabled=1; writer->connected=1;
    productRetry=GetTickCount()-1000;
    ProductStatus status={};
    Check(ReadProductStatus(status),"reads independently mapped host snapshot");
    MEMORY_BASIC_INFORMATION memory={}; VirtualQuery(productView,&memory,sizeof(memory));
    Check(memory.Protect==PAGE_READONLY,"DLL status mapping is OS-enforced read-only");
    writer->sequence=3; Check(!ReadProductStatus(status),"odd sequence rejected"); writer->sequence=4;
    writer->targetPid++; Check(!ReadProductStatus(status),"wrong target rejected"); writer->targetPid--;
    writer->heartbeat=GetTickCount()-6000; Check(!ReadProductStatus(status),"expired host heartbeat rejected"); writer->heartbeat=GetTickCount();
    writer->version[31]=L'x'; Check(!ReadProductStatus(status),"unterminated version rejected"); writer->version[31]=0;
    writer->action[127]=L'x'; Check(!ReadProductStatus(status),"unterminated action rejected"); writer->action[127]=0;
    writer->enabled=0; writer->connected=0;
    Check(ReadProductStatus(status) && !status.enabled && !status.connected,"stop snapshot stays readable without touching game blocks");
    UnmapViewOfFile(writer); CloseHandle(mapping);
    Check(ReadProductStatus(status),"host close does not invalidate live DLL reader view");
    UnmapViewOfFile(productView); CloseHandle(productMapping);
    // Installation transaction must decline a thread already executing patch bytes,
    // resume it on every failure, and remain usable after that thread exits.
    volatile LONG started=0,stop=0;
    BYTE* code=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!code) return 2;
    BYTE worker[]={0xc7,0x05,0,0,0,0,1,0,0,0,0xa1,0,0,0,0,0x85,0xc0,0x74,0xf7,0xc2,4,0};
    DWORD startedAddress=reinterpret_cast<DWORD>(&started),stopAddress=reinterpret_cast<DWORD>(&stop);
    memcpy(worker+2,&startedAddress,4); memcpy(worker+11,&stopAddress,4); memcpy(code,worker,sizeof(worker));
    VirtualProtect(code,4096,PAGE_EXECUTE_READ,&old); FlushInstructionCache(GetCurrentProcess(),code,sizeof(worker));
    HANDLE thread=CreateThread(nullptr,0,reinterpret_cast<LPTHREAD_START_ROUTINE>(code),nullptr,0,nullptr);
    if (!thread) return 2;
    DWORD until=GetTickCount(); while (!started && GetTickCount()-until<1000) Sleep(1);
    Check(started==1,"worker entered controlled instruction window");
    CodeHook first,second; first.target=code; first.length=sizeof(worker); second.target=code+128; second.length=8;
    ProductFreeze freeze;
    Check(!freeze.Acquire(first,second),"installation refuses executing instruction window");
    freeze.Resume(); InterlockedExchange(&stop,1);
    Check(WaitForSingleObject(thread,1000)==WAIT_OBJECT_0,"failed installation resumes worker");
    CloseHandle(thread);
    Check(freeze.Acquire(first,second),"installation transaction can be repeated after worker exits");
    freeze.Resume(); VirtualFree(code,0,MEM_RELEASE);
    char diagnostic[1024];
    ProductFormatEntry("Present",reinterpret_cast<void*>(&FakeRelease),diagnostic,sizeof(diagnostic));
    Check(strstr(diagnostic,"slot=Present") && strstr(diagnostic,"reason=foreign-module") && strstr(diagnostic,"module=ProductTests.exe"),
        "actual address attribution identifies owning image, not loaded-module candidates");
    ProductFormatEntry("Reset",nullptr,diagnostic,sizeof(diagnostic));
    Check(strstr(diagnostic,"reason=unidentified") && strstr(diagnostic,"module=unidentified"),"unknown address never invents NVIDIA attribution");
    void* driver=reinterpret_cast<void*>(Real("D3DPERF_GetStatus"));
    ProductFormatEntry("Reset",driver,diagnostic,sizeof(diagnostic));
    Check(driver && strstr(diagnostic,"module=d3d9.dll") && !strstr(diagnostic,"reason=foreign-module"),"system function attribution remains distinct from foreign slot");
    // Controlled slot experiment: CAS preserves neighbors and rejects a changed slot,
    // but says nothing about the third party's private next-pointer during rehook.
    void* tableFixture[141]={}; void* third=reinterpret_cast<void*>(&FakeRelease);
    void* ours=reinterpret_cast<void*>(&FakeCreate); void* replacement=reinterpret_cast<void*>(&FakeAdapters);
    tableFixture[17]=third; tableFixture[140]=replacement;
    void* saved=tableFixture[17];
    Check(InterlockedCompareExchangePointer(&tableFixture[17],ours,saved)==saved && tableFixture[140]==replacement,
        "slot CAS installs only own pointer without truncating extended table");
    Check(InterlockedCompareExchangePointer(&tableFixture[17],saved,ours)==ours && !memcmp(&tableFixture[17],&saved,sizeof(saved)),
        "controlled slot restore byte-equal");
    tableFixture[17]=replacement;
    Check(InterlockedCompareExchangePointer(&tableFixture[17],ours,saved)!=saved && tableFixture[17]==replacement,
        "changed slot refuses CAS without overwriting third-party value");
    tableFixture[17]=ours;
    void* foreignNext=tableFixture[17]; tableFixture[17]=third;
    Check(saved==third && foreignNext==ours,"rehook can create ours -> third -> ours cycle despite successful initial CAS");
    printf("RESULT product checks=%d failures=%d\n",checks,failures);
    return failures?1:0;
}
