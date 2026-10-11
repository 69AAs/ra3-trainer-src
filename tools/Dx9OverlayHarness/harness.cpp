#include "overlay.h"
#include "device-table.h"
#include "code-hook.h"
#include <stdio.h>
#include <string.h>
static IDirect3D9* api;
static IDirect3DDevice9* dev;
static HWND window;
static HMODULE overlay;
static StartOverlay start;
static StopOverlay stop;
static ReadOverlay readStats;
static FailOverlay failNext;
static bool active, quit, toggle, resetRequested, full, smoke;
static void** realTable;
static void* hostTable[DeviceSlots]; // Read-only diagnostic snapshot, never published.
static CodeHook captureHook;
static_assert(sizeof(hostTable)==119*sizeof(void*),"complete plain D3D9 table");
using PresentFn=HRESULT(WINAPI*)(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*);
static PresentFn realPresent;
static LONG whitePixels;
static HRESULT injectedPresentResult=S_OK;
static bool saveImage;
static wchar_t dllPath[MAX_PATH];
static HRESULT WINAPI CapturePresent(IDirect3DDevice9* d,const RECT* a,const RECT* b,HWND w,const RGNDATA* r) {
    if (smoke && d==dev) {
        IDirect3DSurface9 *back=nullptr,*copy=nullptr; D3DSURFACE_DESC desc={};
        whitePixels=-1;
        if (SUCCEEDED(d->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back)) &&
            SUCCEEDED(back->GetDesc(&desc)) && SUCCEEDED(d->CreateOffscreenPlainSurface(
            desc.Width,desc.Height,desc.Format,D3DPOOL_SYSTEMMEM,&copy,nullptr)) &&
            SUCCEEDED(d->GetRenderTargetData(back,copy))) {
            D3DLOCKED_RECT lock={};
            if (SUCCEEDED(copy->LockRect(&lock,nullptr,D3DLOCK_READONLY))) {
                whitePixels=0;
                // Clamp the scan to the surface we actually locked: the fixed 590x42 window
                // must never read past a smaller back buffer.
                const UINT scanRight=desc.Width<590u?desc.Width:590u,scanBottom=desc.Height<42u?desc.Height:42u;
                for (UINT y=10;y<scanBottom;y++) for (UINT x=10;x<scanRight;x++) {
                    DWORD pixel=*reinterpret_cast<DWORD*>(static_cast<BYTE*>(lock.pBits)+y*lock.Pitch+x*4);
                    if ((pixel&255)>180 && ((pixel>>8)&255)>180 && ((pixel>>16)&255)>180) ++whitePixels;
                }
                if (saveImage) {
                    FILE* f=fopen("frame.bmp","wb");
                    if (f) {
                        BITMAPFILEHEADER h={}; BITMAPINFOHEADER info={};
                        h.bfType=0x4d42; h.bfOffBits=sizeof(h)+sizeof(info); h.bfSize=h.bfOffBits+desc.Width*desc.Height*4;
                        info.biSize=sizeof(info); info.biWidth=desc.Width; info.biHeight=-static_cast<LONG>(desc.Height);
                        info.biPlanes=1; info.biBitCount=32;
                        fwrite(&h,sizeof(h),1,f); fwrite(&info,sizeof(info),1,f);
                        for (UINT y=0;y<desc.Height;y++) fwrite(static_cast<BYTE*>(lock.pBits)+y*lock.Pitch,desc.Width*4,1,f);
                        fclose(f);
                    }
                    saveImage=false;
                }
                copy->UnlockRect();
            }
        }
        if (copy) copy->Release();
        if (back) back->Release();
    }
    return d==dev && FAILED(injectedPresentResult)?injectedPresentResult:realPresent(d,a,b,w,r);
}
static bool InstallCapture(IDirect3DDevice9* target) {
    // ARCHITECTURAL INVARIANT: never write ANY byte of the supplied device,
    // including its vptr. Only code at the actual vtable function address changes.
    void** source=nullptr;
    if (!CodeHook::Read(target,&source,sizeof(source)) ||
        !CodeHook::Read(source,hostTable,sizeof(hostTable))) { puts("capture rejected: unreadable device/table"); return false; }
    for (size_t i=0;i<DeviceSlots;i++) if (!hostTable[i]) {
        printf("capture rejected: null source slot=%u\n",static_cast<unsigned>(i)); return false;
    }
    realTable=source;
    printf("device=%p vtable=%p Present[17]=%p Reset[16]=%p Clear[43]=%p\n",
        target,source,hostTable[17],hostTable[16],hostTable[43]);
    if (!captureHook.Prepare(hostTable[17],reinterpret_cast<void*>(&CapturePresent))) return false;
    realPresent=reinterpret_cast<PresentFn>(captureHook.trampoline);
    if (!captureHook.Install()) { captureHook.Restore(); captureHook.Release(); return false; }
    puts("capture installed: device vptr unchanged; function-body hook");
    return true;
}
static void ProbeDevice(IDirect3DDevice9* target) {
    // COM specifies interface lengths, not an implementation object's private
    // vtable extent. QI can return a DIFFERENT interface pointer. Never scan past
    // the documented length and never claim QI failure proves only 119 slots.
    void** base=*reinterpret_cast<void***>(target);
    printf("probe device=%p vtable=%p Present[17]=%p Reset[16]=%p Clear[43]=%p\n",target,base,base[17],base[16],base[43]);
    const unsigned slots[]={17,16,43};
    for (unsigned slot : slots) {
        BYTE bytes[32]={};
        printf("probe original slot=%u address=%p first32=",slot,base[slot]);
        if (CodeHook::Read(base[slot],bytes,sizeof(bytes))) {
            for (BYTE value:bytes) printf("%02x",value);
        } else printf("unreadable");
        puts("");
    }
    // Diagnostic raw words only, NOT a claim these are valid callable slots.
    // ReadProcessMemory fails safely at inaccessible pages; no speculative calls.
    for (unsigned slot=119;slot<=140;slot++) {
        void* value=nullptr;
        const void* address=reinterpret_cast<const void*>(reinterpret_cast<uintptr_t>(base)+slot*sizeof(void*));
        bool ok=CodeHook::Read(address,&value,sizeof(value));
        printf("probe base-tail[%u] readable=%d raw=%p semantic=unknown\n",slot,ok,value);
    }
    const GUID iidEx={0xb18b10ce,0x2649,0x405a,{0x87,0x0f,0x95,0xf7,0x77,0xd4,0x31,0x3a}};
    IDirect3DDevice9Ex* ex=nullptr;
    HRESULT hr=target->QueryInterface(iidEx,reinterpret_cast<void**>(&ex));
    printf("probe QI(IDirect3DDevice9Ex)=0x%08lx base-interface-slots=119 Ex-interface-slots=%u private-total=unknown\n",
        static_cast<unsigned long>(hr),SUCCEEDED(hr)&&ex?134u:0u);
    if (SUCCEEDED(hr)&&ex) {
        void** table=*reinterpret_cast<void***>(ex);
        printf("probe Ex-device=%p Ex-vtable=%p same-object=%d\n",ex,table,static_cast<void*>(ex)==static_cast<void*>(target));
        for (unsigned i=119;i<134;i++) printf("probe Ex[%u]=%p\n",i,table[i]);
        ex->Release();
    }
}static D3DPRESENT_PARAMETERS Parameters(bool fullscreen) {
    D3DPRESENT_PARAMETERS p={};
    p.Windowed=!fullscreen; p.SwapEffect=D3DSWAPEFFECT_DISCARD; p.hDeviceWindow=window;
    p.BackBufferWidth=800; p.BackBufferHeight=240; p.BackBufferFormat=D3DFMT_X8R8G8B8;
    p.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
    if (fullscreen) { D3DDISPLAYMODE m={}; api->GetAdapterDisplayMode(0,&m);
        p.BackBufferWidth=m.Width; p.BackBufferHeight=m.Height; p.BackBufferFormat=m.Format; }
    return p;
}
static bool Connect(bool injected=false) {
    if (active) return true;
    overlay=injected?GetModuleHandleW(L"Ra3Overlay.dll"):LoadLibraryExW(dllPath,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!overlay) return false;
    start=reinterpret_cast<StartOverlay>(GetProcAddress(overlay,"OverlayStart"));
    stop=reinterpret_cast<StopOverlay>(GetProcAddress(overlay,"OverlayStop"));
    readStats=reinterpret_cast<ReadOverlay>(GetProcAddress(overlay,"OverlayRead"));
    failNext=reinterpret_cast<FailOverlay>(GetProcAddress(overlay,"OverlayFailNext"));
    if (!start || !stop || !readStats || !failNext) { FreeLibrary(overlay); overlay=nullptr; return false; }
    if (!start(dev)) { if (stop(TRUE)) { FreeLibrary(overlay); overlay=nullptr; } else { puts("failed start retained disabled"); active=true; } return false; }
    active=true; return true;
}
static bool Disconnect() {
    if (!active) return true;
    // Single device owner thread; workers joined; no queued/cached callback.
    if (!stop(TRUE)) return false;
    active=false; FreeLibrary(overlay); overlay=nullptr; return true;
}
static HRESULT Frame() {
    if (smoke) puts("Frame: entering Clear[43]");
    HRESULT hr=dev->Clear(0,nullptr,D3DCLEAR_TARGET,D3DCOLOR_XRGB(40,60,90),1,0);
    if (smoke) printf("Frame: Clear=0x%08lx; next Present[17]\n",static_cast<unsigned long>(hr));
    if (FAILED(hr)) return hr;
    return dev->Present(nullptr,nullptr,nullptr,nullptr);
}
static LRESULT CALLBACK WindowProc(HWND h,UINT m,WPARAM w,LPARAM l) {
    if (m==WM_CLOSE) { quit=true; return 0; }
    if (m==WM_KEYDOWN && w=='O') toggle=true;
    if (m==WM_KEYDOWN && w=='R') resetRequested=true;
    if (m==WM_SYSKEYDOWN && w==VK_RETURN && (l&(1L<<29))) { full=!full; resetRequested=true; return 0; }
    if (m==WM_DISPLAYCHANGE) resetRequested=true;
    return DefWindowProcW(h,m,w,l);
}
static int checks, failures;
static void Check(bool ok,const char* name) { ++checks; if (!ok) ++failures; printf("%s %s\n",ok?"PASS":"FAIL",name); fflush(stdout); }
static DWORD WINAPI ForeignRender(void*) { Frame(); return 0; }
int wmain(int argc,wchar_t** argv) {
    setvbuf(stdout,NULL,_IONBF,0);
    puts("Dx9OverlayHarness: starting (stdout unbuffered)");
    smoke=argc>1 && (!wcscmp(argv[1],L"--smoke") || !wcscmp(argv[1],L"--injected"));
    bool injected=argc>1 && !wcscmp(argv[1],L"--injected");
    if (argc>1 && !smoke && wcscmp(argv[1],L"--manual")) return 2;
    DWORD pathLength=GetModuleFileNameW(nullptr,dllPath,MAX_PATH);
    if (!pathLength || pathLength>=MAX_PATH-32) return 2;
    wchar_t* slash=wcsrchr(dllPath,L'\\'); if (!slash) return 2; wcscpy(slash+1,L"Ra3Overlay.dll");
    if (injected) { DWORD begin=GetTickCount(); while (!GetModuleHandleW(L"Ra3Overlay.dll")) {
        if (GetTickCount()-begin>15000) return 3;
        Sleep(10); } }
    WNDCLASSW wc={}; wc.lpfnWndProc=WindowProc; wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"RA3OfflineD3D9";
    if (!RegisterClassW(&wc)) return 4;
    // Show without activation; manual focus comes only from the user's click.
    window=CreateWindowExW(0,wc.lpszClassName,L"Offline D3D9: O toggle, R Reset, Alt+Enter fullscreen",
        WS_OVERLAPPEDWINDOW,100,100,840,300,nullptr,nullptr,wc.hInstance,nullptr);
    if (!window) return 4;
    if (!smoke) ShowWindow(window,SW_SHOWNOACTIVATE);
    api=Direct3DCreate9(D3D_SDK_VERSION); if (!api) return 5;
    D3DDISPLAYMODE desktop={}; HRESULT displayHr=api->GetAdapterDisplayMode(0,&desktop);
    printf("adapters=%u display=0x%08lx format=%u size=%ux%u check=0x%08lx\n",api->GetAdapterCount(),
        static_cast<unsigned long>(displayHr),desktop.Format,desktop.Width,desktop.Height,
        static_cast<unsigned long>(api->CheckDeviceType(0,D3DDEVTYPE_HAL,desktop.Format,D3DFMT_X8R8G8B8,TRUE)));
    D3DPRESENT_PARAMETERS p=Parameters(false);
    HRESULT hr=api->CreateDevice(0,D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING|D3DCREATE_MULTITHREADED,&p,&dev);
    printf("CreateDevice=0x%08lx Windowed=1 x86=%d\n",static_cast<unsigned long>(hr),sizeof(void*)==4);
    if (FAILED(hr)) { api->Release(); DestroyWindow(window); return 6; }
    ProbeDevice(dev);
    if (!InstallCapture(dev)) {
        // A malformed source may even lack Release. Do not dispatch through it.
        // Process exit reclaims this offline device; no overlay has been loaded.
        puts("capture validation/code hook failed: aborting smoke/manual before Frame");
        DestroyWindow(window); return 8;
    }
    if (smoke) {
        Check(SUCCEEDED(Frame()) && whitePixels==0,"baseline has no text");
        for (int i=0;i<12;i++) {
            bool connected=Connect(injected && i==0); Check(connected,"connect"); if (!connected) break;
            Check(!start(dev),"duplicate start rejected");
            saveImage=i==0; hr=Frame(); OverlayStats s={}; readStats(&s);
            printf("pixels=%ld frames=%ld error=0x%08lx\n",whitePixels,s.frames,static_cast<unsigned long>(s.error));
            Check(SUCCEEDED(hr)&&s.frames==1&&whitePixels>100,"Present copied actual glyph pixels");
            p=Parameters(false); Check(SUCCEEDED(dev->Reset(&p)),"windowed Reset");
            Frame(); readStats(&s); Check(s.enabled&&s.resets==1&&s.frames==2,"draw resumes after Reset");
            Check(!stop(FALSE),"no quiescence proof: retain disabled");
            Frame(); readStats(&s); Check(!s.enabled&&s.frames==2&&whitePixels==0,"disabled stops pixels");
            Check(Disconnect(),"restore and unload");
            Check(GetModuleHandleW(L"Ra3Overlay.dll")==nullptr,"DLL actually unloaded");
            Check(*reinterpret_cast<void***>(dev)==realTable && captureHook.Equals(captureHook.patch),"vptr untouched and capture code restored");
        }
        if (Connect()) {
            failNext(); Frame(); OverlayStats s={}; readStats(&s);
            Check(!s.enabled&&FAILED(s.error)&&s.frames==0&&whitePixels==0,"render failure closes drawing");
            Check(Disconnect(),"failure unload at safe boundary");
        } else Check(false,"failure case connect");
        if (Connect()) {
            injectedPresentResult=D3DERR_DEVICELOST; Check(Frame()==D3DERR_DEVICELOST,"Present HRESULT preserved");
            OverlayStats s={}; readStats(&s); Check(!s.enabled&&s.error==D3DERR_DEVICELOST,"device lost disables");
            injectedPresentResult=S_OK; Check(Disconnect(),"lost device safe detach");
        } else Check(false,"lost case connect");
        if (Connect()) {
            HANDLE worker=CreateThread(nullptr,0,ForeignRender,nullptr,0,nullptr);
            Check(worker!=nullptr,"foreign thread created");
            if (worker) { WaitForSingleObject(worker,INFINITE); CloseHandle(worker); }
            OverlayStats s={}; readStats(&s); Check(!s.enabled&&s.error==D3DERR_INVALIDCALL,"foreign render thread fails closed");
            Check(Disconnect(),"joined worker safe detach");
        } else Check(false,"thread case connect");
        printf("RESULT checks=%d failures=%d\n",checks,failures);
    } else {
        if (!Connect()) { printf("connect failed\n"); failures++; quit=true; }
        while (!quit) {
            MSG msg; while (PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&msg); DispatchMessageW(&msg); }
            if (toggle) { toggle=false; if (active) Disconnect(); else Connect(); }
            if (resetRequested) { resetRequested=false; p=Parameters(full); hr=dev->Reset(&p);
                printf("Reset Windowed=%d hr=0x%08lx\n",!full,static_cast<unsigned long>(hr)); }
            hr=Frame();
            if (active) { OverlayStats s={}; readStats(&s); if (!s.enabled) {
                printf("overlay disabled hr=0x%08lx\n",static_cast<unsigned long>(s.error)); Disconnect(); } }
            if (FAILED(hr) && dev->TestCooperativeLevel()==D3DERR_DEVICENOTRESET) {
                p=Parameters(full); dev->Reset(&p); }
            Sleep(16);
        }
    }
    if (!Disconnect()) { printf("retained disabled until process exit\n"); return 7; }
    if (!captureHook.Restore()) { puts("capture retained disabled until process exit"); return 7; }
    captureHook.Release(); dev->Release(); api->Release(); DestroyWindow(window);
    return failures?1:0;
}
