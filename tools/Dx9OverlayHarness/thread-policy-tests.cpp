// Synthetic COM, real Windows threads, actual product Present; no GPU/game claim.
#define device fixtureDevice
#define wmain TextureFixtureMain
#include "texture-draw-tests.cpp"
#undef wmain
#undef device
#define OVERLAY_PRODUCT
#include "overlay.cpp"
bool ProductEntryOwned(void*) { return false; }
void ProductDescribeEntry(const char*,void*) {}
static int forwarded;
static HRESULT WINAPI HostPresent(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*) { ++forwarded; return S_OK; }
static HRESULT WINAPI HostReset(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*) { return S_OK; }
static DWORD WINAPI Invoke(void*) { return static_cast<DWORD>(Present(device,nullptr,nullptr,nullptr,nullptr)); }
static void Run(bool different) {
    if(!different) { Check(Invoke(nullptr)==S_OK,"host result unchanged"); return; }
    HANDLE thread=CreateThread(nullptr,0,Invoke,nullptr,0,nullptr);
    Check(thread!=nullptr,"real worker thread created");
    if(!thread) return;
    const DWORD waitResult=WaitForSingleObject(thread,10000);
    Check(waitResult==WAIT_OBJECT_0,"worker callback completed");
    // Never touch the shared fixture state while the worker may still be running.
    if(waitResult!=WAIT_OBJECT_0) { CloseHandle(thread); return; }
    DWORD result=1; GetExitCodeThread(thread,&result); CloseHandle(thread);
    Check(result==S_OK,"worker preserves host result");
}
int wmain() {
    setvbuf(stdout,nullptr,_IONBF,0);
    Initialize(); device=reinterpret_cast<IDirect3DDevice9*>(&fixtureDevice);
    nextPresent=HostPresent; nextReset=HostReset; owner=GetCurrentThreadId(); ownerSource="CreateDevice-entry";
    ProductStatus status={}; status.magic=0x3544534f;status.size=sizeof(status);status.targetPid=GetCurrentProcessId();
    status.enabled=status.connected=1;productView=&status;
    puts("EVIDENCE fixture-only actual product callback; synthetic device; real worker thread; no hook installation/GPU/game proof");
    for(bool mt : {false,true}) for(bool different : {false,true}) {
        creationFlags=D3DCREATE_SOFTWARE_VERTEXPROCESSING|(mt?D3DCREATE_MULTITHREADED:0);
        stats={1,0,0,0,S_OK}; calls=0;draws=0;forwarded=0;failAt=-1;drawDiagnostics=true;loggedThreadCount=0;
        status.heartbeat=GetTickCount();status.enabled=1;
        D3DDEVICE_CREATION_PARAMETERS cp{0,D3DDEVTYPE_HAL,nullptr,creationFlags};
        OverlayCreationCall context{device,owner,creationFlags,D3DDEVTYPE_HAL,nullptr,reinterpret_cast<HWND>(0x1234),S_OK};
        char line[768];OverlayFormatCreation(line,sizeof(line),context,cp,S_OK);ProductLog(line);puts(line);
        printf("CASE different=%u multithreaded=%u expectedAllowed=%u\n",different,mt,!different||mt);
        const State before=state;Run(different);
        const bool allowed=!different||mt;
        Check(forwarded==1,"original Present forwarded exactly once");
        Check((stats.error==S_OK)==allowed,"thread admission matches four-way matrix");
        Check(allowed?(calls>0 && draws==1 && stats.frames==1):(calls==0 && draws==0 && stats.frames==0),"admission controls actual renderer entry, not only predicate");
        EqualState(before,"thread-matrix",false);
        if(!allowed) {
            Run(different);Check(calls==0 && !stats.enabled,"rejected OSD never resumes on next frame");
            Check(Reset(device,&present)==S_OK && !stats.enabled,"Reset does not revive thread failure");
        } else {
            status.enabled=0;calls=0;Run(different);Check(!stats.enabled && calls==0,"disabled status honored on permitted Present thread");
            status.enabled=1;Run(different);Check(stats.enabled && calls>0,"explicit reenable works after status-only stop");
            Check(Reset(device,&present)==S_OK && stats.enabled,"successful owner Reset preserves enabled OSD");
        }
    }
    Check(!OverlayThreadAllowed(0,1,D3DCREATE_MULTITHREADED),"unknown creator cannot grant admission");
    char line[768]; D3DDEVICE_CREATION_PARAMETERS cp={};
    OverlayCreationCall context{device,owner,4,D3DDEVTYPE_HAL,nullptr,nullptr,S_OK};
    OverlayFormatCreation(line,sizeof(line),context,cp,E_FAIL);
    Check(strstr(line,"flagsValid=0") && strstr(line,"queryHr=80004005"),"failed creation query is explicit, not valid zero flags");
    // Actual ProductLog failure must not gate a permitted cross-thread draw.
    char oldTemp[32768]={},oldTmp[32768]={},missing[MAX_PATH];
    GetEnvironmentVariableA("TEMP",oldTemp,sizeof(oldTemp));GetEnvironmentVariableA("TMP",oldTmp,sizeof(oldTmp));
    snprintf(missing,sizeof(missing),"C:\\Ra3-r35-nonexistent-%lu-%lu\\logs\\",GetCurrentProcessId(),GetTickCount());
    Check(GetFileAttributesA(missing)==INVALID_FILE_ATTRIBUTES,"log failure path absent");
    SetEnvironmentVariableA("TEMP",missing);SetEnvironmentVariableA("TMP",missing);
    creationFlags=D3DCREATE_MULTITHREADED;stats={1,0,0,0,S_OK};calls=0;draws=0;loggedThreadCount=0;status.heartbeat=GetTickCount();
    Run(true);Check(stats.error==S_OK && draws==1,"native log open failure leaves cross-thread draw unchanged");
    SetEnvironmentVariableA("TEMP",oldTemp[0]?oldTemp:nullptr);SetEnvironmentVariableA("TMP",oldTmp[0]?oldTmp:nullptr);
    stats={1,0,0,0,S_OK};calls=0;failAt=1;status.heartbeat=GetTickCount();
    Run(true);Check(stats.error==E_FAIL && !stats.enabled,"MT worker renderer failure closes OSD");
    failAt=-1;calls=0;Run(true);Check(calls==0,"MT worker failure remains closed on next frame");
    Check(Reset(device,&present)==S_OK && !stats.enabled,"Reset cannot revive failed MT worker draw");
    auto retained=device;device=nullptr;
    OverlayCreationCall creation{retained,owner,4,D3DDEVTYPE_HAL,nullptr,reinterpret_cast<HWND>(0x1234),S_OK};
    overlayCreationCall=&creation;calls=0;failAt=1;
    Check(!OverlayStart(retained) && calls==1 && !device,"actual creation query failure refuses installation before hooks");
    failAt=-1;calls=0;
    Check(!OverlayStart(retained) && calls==1 && !device,"actual creation query observed before unchanged entry gate refusal");
    overlayCreationCall=nullptr;device=retained;
    // Capture native records for the real managed relay fixture.
    char path[MAX_PATH],folder[MAX_PATH];GetTempPathA(MAX_PATH,folder);
    snprintf(path,sizeof(path),"%sRa3Overlay-%lu.log",folder,GetCurrentProcessId());
    Check(CopyFileA(path,"thread-native.log",FALSE)!=FALSE,"native thread evidence copied");
    productView=nullptr;device=nullptr;
    printf("RESULT thread-policy checks=%d failures=%d skipped=0\n",checks,failures);
    return failures?1:0;
}
