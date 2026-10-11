// Actual product callback compiled with the synthetic COM device. This does not
// install hooks or prove GPU pixels; existing entry/transaction tests cover hooks.
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
static HRESULT hostResult=S_OK;
static HRESULT WINAPI HostPresent(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*) { ++forwarded;return hostResult; }
static HRESULT WINAPI HostReset(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*) {return S_OK;}
int wmain() {
    if(TextureFixtureMain()) return 1;
    puts("EVIDENCE actual-product-Present-callback with synthetic device; installation not exercised here");
    auto d=reinterpret_cast<IDirect3DDevice9*>(&fixtureDevice);
    ProductStatus status={};status.magic=0x3544534f;status.size=sizeof(status);status.targetPid=GetCurrentProcessId();
    status.heartbeat=GetTickCount();status.enabled=1;status.connected=1;
    productView=&status;
    device=d;owner=GetCurrentThreadId();nextPresent=HostPresent;nextReset=HostReset;
    stats={0,0,0,0,S_OK};stats.enabled=1;failAt=-1;calls=0;
    const State before=state;
    Check(Present(d,nullptr,nullptr,nullptr,nullptr)==S_OK && stats.frames==1 && forwarded==1,"product callback counts new textured draw and forwards host Present exactly once");
    EqualState(before,"product-success",false);
    calls=0;failAt=1;
    Check(Present(d,nullptr,nullptr,nullptr,nullptr)==S_OK && stats.enabled==0 && stats.error==E_FAIL && forwarded==2,"renderer failure disables OSD but preserves host Present result");
    EqualState(before,"product-failure",false);
    calls=0;failAt=-1;
    Check(Present(d,nullptr,nullptr,nullptr,nullptr)==S_OK && calls==0 && stats.frames==1 && forwarded==3,"failed OSD does not resume on next requested frame");
    Check(Reset(d,&present)==S_OK && stats.enabled==0 && stats.error==E_FAIL,"successful Reset cannot revive failed OSD");
    stats.error=S_OK;stats.enabled=1;hostResult=D3DERR_DEVICELOST;
    Check(Present(d,nullptr,nullptr,nullptr,nullptr)==D3DERR_DEVICELOST && stats.enabled==0 && stats.error==D3DERR_DEVICELOST && forwarded==4,"host Present failure forwarded unchanged and OSD stopped");
    hostResult=S_OK;stats.error=S_OK;stats.enabled=1;
    Check(!OverlayStop(FALSE) && !stats.enabled && device==d,"stop reports not-unloaded and retains device until process exit");
    char oldTemp[32768]={},oldTmp[32768]={},missing[MAX_PATH];
    GetEnvironmentVariableA("TEMP",oldTemp,sizeof(oldTemp));GetEnvironmentVariableA("TMP",oldTmp,sizeof(oldTmp));
    // '|' is not a legal filename character, so this path can never be created or opened: the
    // ProductLog failure is then deterministic instead of depending on C:\ not being writable.
    snprintf(missing,sizeof(missing),"C:\\Ra3-r34-nonexistent-%lu\\lo|gs\\",GetCurrentProcessId());
    Check(GetFileAttributesA(missing)==INVALID_FILE_ATTRIBUTES,"failed-log fixture path can never be opened");
    SetEnvironmentVariableA("TEMP",missing);SetEnvironmentVariableA("TMP",missing);
    stats.error=S_OK;stats.enabled=1;drawDiagnostics=true;
    Check(Present(d,nullptr,nullptr,nullptr,nullptr)==S_OK && stats.enabled && stats.error==S_OK,"actual ProductLog CreateFile failure leaves rendering and host result unchanged");
    SetEnvironmentVariableA("TEMP",oldTemp[0]?oldTemp:nullptr);SetEnvironmentVariableA("TMP",oldTmp[0]?oldTmp:nullptr);
    scene=true;calls=0;Trace trace{nullptr,false};
    Check(TextureOsd::Draw(d,L"nested scene",trace)==D3DERR_INVALIDCALL && scene,"BeginScene refusal does not EndScene caller's scene");
    scene=false;EqualState(before,"existing-scene",false);
    productView=nullptr;device=nullptr;
    printf("RESULT product-texture checks=%d failures=%d skipped=0\n",checks,failures);
    return failures?1:0;
}
