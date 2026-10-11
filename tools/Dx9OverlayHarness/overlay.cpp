#include "overlay.h"
#include "code-hook.h"
#include <string.h>
#ifdef OVERLAY_PRODUCT
#include "product-status.h"
#include "product-freeze.h"
#include "texture-draw.h"
#include "thread-policy.h"
#include "engine-lua.h"
#ifdef OVERLAY_PANEL
#include "panel-draw.h"
#include "panel-input.h"
static PanelOsd::Reader panelReader;
static PanelOsd::Cache panelCache;
static const char* panelReason=nullptr;
static unsigned panelPublicationLogs;
// M2b: mouse-only interception, active strictly while the panel mode word is 1.
static PanelInput::MouseHook panelInput;
static bool panelInputReady, panelInputEventReady;
// Every path that loses the panel mapping must also let go of the input hook. The event
// block lives inside that view, so a hook that still points at it both faults on the next
// swallowed message (WER: d3d9.dll AV writing _event->sequence) and eats the player's
// mouse after the panel it belonged to is gone. Order matters: drop the pointer first,
// then restore the game's procedure.
static void ReleasePanelInput() noexcept {
    if(!panelInputReady) return;
    panelInput.ReleaseEvent();
    panelInput.Sync(false);
    panelInputEventReady=false;
}
#endif
extern bool ProductEntryOwned(void*);
extern void ProductDescribeEntry(const char*,void*);
#endif
static_assert(sizeof(void*) == 4, "x86 only");
using PresentFn = HRESULT (WINAPI *)(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*);
using ResetFn = HRESULT (WINAPI *)(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*);
static SRWLOCK gate = SRWLOCK_INIT;
static IDirect3DDevice9* device;
static void** original;
static CodeHook presentHook, resetHook;
static PresentFn nextPresent;
static ResetFn nextReset;
static OverlayStats stats;
static LONG failNext;
static DWORD owner;
static LONG disableGeneration;
#ifdef OVERLAY_PRODUCT
extern "C" BOOL __cdecl OverlayStop(BOOL) noexcept;
// Serialized by gate. Emit complete attempts on first frame, Reset or failure.
static char drawLog[160][512];
static unsigned drawLogCount;
static bool drawDiagnostics=true;
static DWORD creationFlags;
static DWORD loggedThreads[16];
static unsigned loggedThreadCount;
static const char* ownerSource="OverlayStart-caller";
static bool ThreadAllowed(DWORD tid) noexcept { return OverlayThreadAllowed(owner,tid,creationFlags); }
// Called under gate. Record each observed thread once, with bounded storage/IO.
static void LogPresentThread(DWORD tid) noexcept {
    for(unsigned i=0;i<loggedThreadCount;++i) if(loggedThreads[i]==tid) return;
    if(loggedThreadCount==16) return;
    loggedThreads[loggedThreadCount++]=tid;
    char line[512];
    snprintf(line,sizeof(line),"osd-draw-thread role=Present tid=%lu creationTid=%lu flags=0x%08lx match=%u multithreaded=%u allowed=%u device=%p source=%s decision=%s",
        tid,owner,creationFlags,tid==owner?1u:0u,(creationFlags&D3DCREATE_MULTITHREADED)?1u:0u,
        ThreadAllowed(tid)?1u:0u,device,ownerSource,
        ThreadAllowed(tid)?"allow":"local-policy-needs-readings");
    ProductLog(line);
}
static void BufferDrawLog(const char* line) noexcept {
    if(drawLogCount<160) snprintf(drawLog[drawLogCount++],512,"%s",line);
}
#endif

static HRESULT Draw(IDirect3DDevice9* d) noexcept {
#ifdef OVERLAY_PRODUCT
    ProductStatus state={};
    if (!ReadProductStatus(state) || !state.enabled || !state.connected) return S_FALSE;
#ifdef OVERLAY_PANEL
    const HRESULT read=panelReader.Read();
    if(read!=S_OK) {
        // Only a mapping that is really gone (or a host that stopped publishing) ends the
        // takeover. A torn or rejected single snapshot must not flap the hook on and off.
        if(!panelReader.View() || panelReader.pendingRelease) {
            ReleasePanelInput();
            panelReader.CommitPendingRelease();
        }
        if(!panelReason || strcmp(panelReason,panelReader.reason)) {
            char line[256];snprintf(line,sizeof(line),"osd-panel-step name=ReadMapping hr=%08lx reason=%s message=面板映射不可用，停止绘制",static_cast<unsigned long>(read),panelReader.reason);
            ProductLog(line);panelReason=panelReader.reason;
        }
        return read;
    }
    if(panelReason) drawDiagnostics=true;
    panelReason=nullptr;
    // M2b: the event block address is refreshed on every readable frame. A reconnect
    // reopens the mapping at a new address, so keeping only the first pointer would
    // leave the hook writing into the retired view. Plain store, no API call.
    if(panelInputReady && panelReader.EventBase()) {
        panelInput.SetEventBase(panelReader.EventBase());
        panelInputEventReady=true;
    }
    // M2b: attach/detach is driven purely by the host's mode word. The window
    // handle was captured in OverlayStart, so this path adds no API call.
    if(panelInputReady) panelInput.Sync(PanelOsd::PanelMode(panelReader.header));
    const bool changed=panelCache.epoch!=panelReader.header.epoch || panelCache.generation!=panelReader.header.generation;
    drawLogCount=0;
    TextureOsd::Trace trace{BufferDrawLog,true,nullptr,"osd-panel"};
    trace.Step("ReadMapping",S_OK);
    const HRESULT result=InterlockedExchange(&failNext,0)?trace.Step("InjectedFailure",E_FAIL):
        PanelOsd::Draw(d,panelReader.header,panelReader.pixels,panelCache,trace,
                    PanelOsd::PanelMode(panelReader.header) && panelInput.Active() && panelInput.Mode(),
                    panelInput.CursorX(),panelInput.CursorY());
    if(drawDiagnostics || FAILED(result)) {
        for(unsigned i=0;i<drawLogCount;++i) ProductLog(drawLog[i]);
        drawDiagnostics=false;
    } else if(changed && panelPublicationLogs<128) {
        ++panelPublicationLogs;
        // Routine dirty updates need three records, not a complete state transaction.
        // Bound successful update logging; failures and recovery remain unconditional.
        for(unsigned i=0;i<drawLogCount;++i)
            if(!strncmp(drawLog[i],"osd-panel-desc w=",17) || !strncmp(drawLog[i],"osd-panel-uploaded",18) || !strcmp(drawLog[i],"osd-panel-drawn")) ProductLog(drawLog[i]);
    }
    if(FAILED(result)) {
        char line[256];snprintf(line,sizeof(line),"osd-panel-step name=Stopped hr=%08lx reason=%s message=面板绘制失败，已关闭；不影响游戏与全撤",static_cast<unsigned long>(result),trace.failed?trace.failed:"unknown");ProductLog(line);
    }
    return result;
#else
    WCHAR text[256];
    swprintf(text,256,L"RA3 已连接 %ls | 已开 %lu | %ls",state.version,state.count,state.action);
    drawLogCount=0;
    TextureOsd::Trace trace{BufferDrawLog,true};
    if (InterlockedExchange(&failNext,0)) {
        TextureOsd::Trace immediate{ProductLog,true}; return immediate.Step("InjectedFailure",E_FAIL);
    }
    const HRESULT result=TextureOsd::Draw(d,text,trace);
    if(drawDiagnostics || FAILED(result)) {
        for(unsigned i=0;i<drawLogCount;++i) ProductLog(drawLog[i]);
        drawDiagnostics=false;
    }
    if (FAILED(result)) {
        char failure[192]; snprintf(failure,sizeof(failure),"osd-draw-failure-detail name=%s hr=%08lx",trace.failed?trace.failed:"unknown",static_cast<unsigned long>(result)); ProductLog(failure);
    }
    return result;
#endif
#else
    const WCHAR* text=L"RA3 状态：离线覆盖层原型 | 已连接 | 功能未启用";
    if (InterlockedExchange(&failNext,0)) return E_FAIL;
    IDirect3DSurface9 *back=nullptr, *source=nullptr;
    HRESULT hr=d->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back);
    if (FAILED(hr)) return hr;
    D3DSURFACE_DESC desc={};
    hr=back->GetDesc(&desc);
    if (SUCCEEDED(hr) && (desc.Format!=D3DFMT_X8R8G8B8 ||
        desc.MultiSampleType!=D3DMULTISAMPLE_NONE || desc.Width<600 || desc.Height<48)) hr=D3DERR_NOTAVAILABLE;
    if (SUCCEEDED(hr)) hr=d->CreateOffscreenPlainSurface(580,32,desc.Format,D3DPOOL_SYSTEMMEM,&source,nullptr);
    if (SUCCEEDED(hr)) {
        HDC dc=nullptr;
        hr=source->GetDC(&dc);
        if (SUCCEEDED(hr)) {
            RECT rect={0,0,580,32};
            HBRUSH brush=CreateSolidBrush(RGB(20,24,32));
            HFONT font=CreateFontW(-20,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,
                OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei");
            HGDIOBJ old=font?SelectObject(dc,font):nullptr;
            BOOL ok=brush && font && old && old!=HGDI_ERROR;
            if (ok) ok=FillRect(dc,&rect,brush)!=0 && SetBkMode(dc,TRANSPARENT)!=0 &&
                SetTextColor(dc,RGB(240,244,250))!=CLR_INVALID &&
                DrawTextW(dc,text,-1,&rect,
                    DT_SINGLELINE|DT_VCENTER|DT_LEFT|DT_NOPREFIX)!=0;
            if (old && old!=HGDI_ERROR) SelectObject(dc,old);
            if (font) DeleteObject(font);
            if (brush) DeleteObject(brush);
            HRESULT release=source->ReleaseDC(dc);
            hr=ok?release:E_FAIL;
        }
    }
    if (SUCCEEDED(hr)) { POINT point={10,10}; hr=d->UpdateSurface(source,nullptr,back,&point); }
    if (source) source->Release();
    back->Release();
    return hr;
#endif // The historical offline contract DLL retains its old surface-copy path.
}
static HRESULT WINAPI Present(IDirect3DDevice9* d,const RECT* a,const RECT* b,HWND w,const RGNDATA* r) noexcept {
    InterlockedIncrement(&stats.callbacks);
#ifdef OVERLAY_PRODUCT
    InterlockedIncrement(&EngineLua::presentFrames);
    EngineLua::DrainProbe();
    EngineLua::DrainObserved();
    EngineLua::DrainCommandNote();
    EngineLua::log = &ProductLog;
    bool productOwner=false;
    bool firstDraw=false;
#endif
    // Never block a render callback behind lifecycle work or a reentrant Reset.
    if (TryAcquireSRWLockExclusive(&gate)) {
#ifdef OVERLAY_PRODUCT
        productOwner=d==device;
        if(productOwner) {
            const DWORD tid=GetCurrentThreadId();
            LogPresentThread(tid);
            ProductStatus state={};
            const bool requested=ReadProductStatus(state) && state.enabled && state.connected;
            static bool stopLogged=false;
            if(!requested) {
#ifdef OVERLAY_PANEL
                ReleasePanelInput();
                panelReader.Close();
#endif
                stats.enabled=0; ++disableGeneration;
                if(!stopLogged) { ProductLog("osd-stop-disabled retained-until-process-exit"); stopLogged=true; }
            } else {
                stopLogged=false;
                if(stats.error==S_OK) stats.enabled=1;
            }
        }
#endif
        if (stats.enabled && d==device) {
#ifdef OVERLAY_PRODUCT
            const bool allowed=ThreadAllowed(GetCurrentThreadId());
            HRESULT hr=allowed?Draw(d):D3DERR_INVALIDCALL;
            if(!allowed) {
                TextureOsd::Trace trace{ProductLog,true}; trace.Step("OwnerThread",hr);
                ProductLog("osd-draw-failure-detail name=OwnerThread hr=8876086c reason=local-policy-needs-readings");
            }
#else
            HRESULT hr=GetCurrentThreadId()==owner?Draw(d):D3DERR_INVALIDCALL;
#endif
            if (FAILED(hr)) {
                stats.error=hr; stats.enabled=0; ++disableGeneration;
#ifdef OVERLAY_PRODUCT
                char failure[96]; snprintf(failure,sizeof(failure),"osd-draw-failed hr=%08lx disabled",static_cast<unsigned long>(hr)); ProductLog(failure);
#endif
#ifdef OVERLAY_PANEL
                // The overlay is now disabled for good (stats.error keeps the failure),
                // so Draw will never run again to re-sync the panel. Drop the input hook
                // here too, or the game keeps having numpad keys and in-panel clicks
                // swallowed by a window procedure that will never be released again.
                ReleasePanelInput();
#endif
            }
            else if (hr==S_OK) {
                ++stats.frames;
#ifdef OVERLAY_PRODUCT
                firstDraw=stats.frames==1;
#endif
            }
        }
        ReleaseSRWLockExclusive(&gate);
    }
    HRESULT hr=nextPresent(d,a,b,w,r);
#ifdef OVERLAY_PRODUCT
    if (firstDraw || (productOwner && FAILED(hr))) {
        TextureOsd::Trace trace{ProductLog,true}; trace.Step("Present-blit",hr);
    }
    if (firstDraw && SUCCEEDED(hr)) ProductLog("osd-frame-presented");
    static HRESULT loggedPresentError=S_OK;
    if (productOwner && FAILED(hr) && hr!=loggedPresentError) {
        loggedPresentError=hr; ProductLog("osd-present-failed disabled");
    }
#endif
    if (FAILED(hr) && d==device) {
        AcquireSRWLockExclusive(&gate);
        stats.enabled=0; stats.error=hr; ++disableGeneration;
        ReleaseSRWLockExclusive(&gate);
    }
    InterlockedDecrement(&stats.callbacks);
    return hr; // Preserve host result; never retry host Present.
}
static HRESULT WINAPI Reset(IDirect3DDevice9* d,D3DPRESENT_PARAMETERS* p) noexcept {
    InterlockedIncrement(&stats.callbacks);
    if (d!=device) { HRESULT hr=nextReset(d,p); InterlockedDecrement(&stats.callbacks); return hr; }
    AcquireSRWLockExclusive(&gate);
    LONG enabled=stats.enabled, generation=disableGeneration; stats.enabled=0; ++stats.resets;
    ReleaseSRWLockExclusive(&gate);
    // No retained default-pool surface or DC exists. Host owns Reset thread policy.
    HRESULT hr=nextReset(d,p);
    AcquireSRWLockExclusive(&gate);
#ifdef OVERLAY_PRODUCT
    drawDiagnostics=true;
#ifdef OVERLAY_PANEL
    panelCache.generation=0; // Managed pool survives Reset; refresh its contents on the next allowed draw.
#endif
#endif
    if (FAILED(hr)) { stats.error=hr; ++disableGeneration; }
    else if (enabled && generation==disableGeneration
#ifdef OVERLAY_PRODUCT
        && ThreadAllowed(GetCurrentThreadId())
#else
        && GetCurrentThreadId()==owner
#endif
    ) stats.enabled=1;
    ReleaseSRWLockExclusive(&gate);
    InterlockedDecrement(&stats.callbacks);
    return hr;
}
extern "C" __declspec(dllexport) BOOL __cdecl OverlayStart(IDirect3DDevice9* d) noexcept {
    AcquireSRWLockExclusive(&gate);
    if (!d || device) { ReleaseSRWLockExclusive(&gate); return FALSE; }
    D3DDEVICE_CREATION_PARAMETERS cp={};
    const HRESULT creationHr=d->GetCreationParameters(&cp);
#ifdef OVERLAY_PRODUCT
#ifdef OVERLAY_PANEL
    // M2b: capture the game window once, here, where the creation parameters are
    // already read. The per-frame panel path therefore adds no API call.
    if(SUCCEEDED(creationHr) && cp.hFocusWindow) {
        panelInput.Init(&panelReader.header,nullptr,&ProductLog);
        panelInput.SetGameWindow(cp.hFocusWindow);
        panelInputReady=true;
        ProductLog("osd-input-window-known");
    } else {
        ProductLog("osd-input-window-unknown");
    }
#endif
    const auto call=overlayCreationCall;
    if(!call || call->device!=d) {
        ReleaseSRWLockExclusive(&gate); ProductLog("osd-install-refused creation-thread-unverified"); return FALSE;
    }
    if(call && call->device==d) {
        char line[768]; OverlayFormatCreation(line,sizeof(line),*call,cp,creationHr); ProductLog(line);
    }
#endif
    if (FAILED(creationHr)) { ReleaseSRWLockExclusive(&gate); return FALSE; }
    // Host has closed/joined ALL callers of the shared function addresses.
    // INVARIANT: never write device object memory or its vptr, even for Ex.
    original=*reinterpret_cast<void***>(d);
#ifdef OVERLAY_PRODUCT
    // Foreign entry chains are supported only by the cooperative offline harness.
    // Product refuses them rather than claiming ownership of another overlay.
    BYTE p=0,r=0;
    ProductDescribeEntry("Present",original[17]);
    ProductDescribeEntry("Reset",original[16]);
    // Read-only candidate evidence; this does not install an EndScene hook.
    ProductDescribeEntry("EndScene",original[42]);
    if (!ProductEntryOwned(original[17]) || !ProductEntryOwned(original[16]) ||
        !CodeHook::Read(original[17],&p,1) || !CodeHook::Read(original[16],&r,1) || p==0xe9 || r==0xe9) {
        ReleaseSRWLockExclusive(&gate); ProductLog("osd-install-refused foreign-entry"); return FALSE;
    }
#endif
    if (!presentHook.Prepare(original[17],reinterpret_cast<void*>(&Present)) ||
        !resetHook.Prepare(original[16],reinterpret_cast<void*>(&Reset)) || presentHook.Overlaps(resetHook)) {
        presentHook.Release(); resetHook.Release(); ReleaseSRWLockExclusive(&gate); return FALSE;
    }
#ifdef OVERLAY_PRODUCT
    if (!ProductEntryOwned(original[17]) || !ProductEntryOwned(original[16]) ||
        !presentHook.Equals(presentHook.before) || !resetHook.Equals(resetHook.before)) {
        presentHook.Release(); resetHook.Release(); ReleaseSRWLockExclusive(&gate);
        ProductLog("osd-install-refused entry-changed-during-prepare"); return FALSE;
    }
#endif
    nextPresent=reinterpret_cast<PresentFn>(presentHook.trampoline);
    nextReset=reinterpret_cast<ResetFn>(resetHook.trampoline);
    stats={0,0,0,0,S_OK}; owner=GetCurrentThreadId(); failNext=0;
#ifdef OVERLAY_PRODUCT
    creationFlags=cp.BehaviorFlags; loggedThreadCount=0;
    if(call && call->device==d) { owner=call->tid; ownerSource="CreateDevice-entry"; }
    else { creationFlags=0; ownerSource="OverlayStart-caller-unverified"; }
#endif
    device=d; d->AddRef();
#ifdef OVERLAY_PRODUCT
    ProductFreeze freeze;
    if (!freeze.Acquire(presentHook,resetHook)) {
        freeze.Resume(); device=nullptr; d->Release(); presentHook.Release(); resetHook.Release();
        ReleaseSRWLockExclusive(&gate); ProductLog("osd-install-refused thread-window"); return FALSE;
    }
#endif
    if (!presentHook.Install() || !resetHook.Install()) {
        bool a=resetHook.Restore(), b=presentHook.Restore();
#ifdef OVERLAY_PRODUCT
        freeze.Resume();
#endif
        if (a && b) { resetHook.Release(); presentHook.Release(); device=nullptr; d->Release(); }
        else puts("overlay installation rollback unproved: retain disabled");
        ReleaseSRWLockExclusive(&gate); return FALSE;
    }
#ifdef OVERLAY_PRODUCT
    // Read-only layout probe: one hook, installed in the frozen window, takes two state snapshots
    // and never calls into the engine. No logging while threads are suspended.
    int engineLuaProbe=0;
    {
        void* module=GetModuleHandleW(nullptr);
        if(!EngineLua::Bind(module)) engineLuaProbe=-1;
        else if(!EngineLua::InstallActionTap(module)) engineLuaProbe=-2;
        else engineLuaProbe=1;
    }
    freeze.Resume();
    ProductLog(engineLuaProbe==1 ? "osd-engine-lua probe-hook-installed"
              : engineLuaProbe==-1 ? "osd-engine-lua probe-bind-refused"
                                   : "osd-engine-lua probe-hook-refused");
    // 每个入口是"扫特征码"还是"回退写死地址"定出来的 —— 换版本/加载器时看这一行就知道退化没有。
    if (EngineLua::bindLine[0] != '\0') ProductLog(EngineLua::bindLine);
    {
        void* module=GetModuleHandleW(nullptr);
        if(EngineLua::DumpCodeCandidates(module)) ProductLog(EngineLua::codeLine);
    }
    ProductLog("osd-hook-installed device-vptr-unchanged");
    // 命令通道的工作线程：只在门控文件存在时才真的做事（见 engine-lua.h 的线程纪律）。
    EngineLua::StartCommandThread();
    ProductLog("osd-installed");
#endif
    stats.enabled=1;
    ReleaseSRWLockExclusive(&gate); return TRUE;
}
extern "C" __declspec(dllexport) BOOL __cdecl OverlayStop(BOOL hostQuiescent) noexcept {
#ifdef OVERLAY_PRODUCT
    hostQuiescent=FALSE; // Product never accepts a caller's unsupported unload assertion.
#endif
    AcquireSRWLockExclusive(&gate); stats.enabled=0; ++disableGeneration;
    if (!device) { ReleaseSRWLockExclusive(&gate); return TRUE; }
    // callbacks==0 ALONE is NOT a proof: a thread can have fetched our entry.
    if (!hostQuiescent || stats.callbacks || GetCurrentThreadId()!=owner) {
        ReleaseSRWLockExclusive(&gate); return FALSE;
    }
    if (*reinterpret_cast<void***>(device)!=original) {
        ReleaseSRWLockExclusive(&gate); return FALSE; // changed host ownership
    }
#ifdef OVERLAY_PANEL
    // Restore the game's window procedure. A refusal (somebody subclassed after
    // us) is logged but never blocks the 22-hook restore the owner asked for.
    if(panelInputReady) { ReleasePanelInput(); panelInputReady=false; }
    panelReader.CommitPendingRelease();
#endif
    bool resetRestored=resetHook.Restore(), presentRestored=presentHook.Restore();
    if (!resetRestored || !presentRestored) { ReleaseSRWLockExclusive(&gate); return FALSE; }
    resetHook.Release(); presentHook.Release();
    IDirect3DDevice9* retiring=device; device=nullptr;
    ReleaseSRWLockExclusive(&gate); retiring->Release(); return TRUE;
}
extern "C" __declspec(dllexport) void __cdecl OverlayRead(OverlayStats* out) noexcept {
    if (!out) return;
    AcquireSRWLockExclusive(&gate); *out=stats; ReleaseSRWLockExclusive(&gate);
}
extern "C" __declspec(dllexport) void __cdecl OverlayFailNext() noexcept { InterlockedExchange(&failNext,1); }
// No D3D/GDI/thread/wait/cleanup work in loader lock.
BOOL WINAPI DllMain(HINSTANCE,DWORD,LPVOID) { return TRUE; }
