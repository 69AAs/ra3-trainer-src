// Actual DLL, synthetic COM device. This tests ABI/lifecycle/fail-close only;
// it is NOT evidence that a hardware backbuffer or fullscreen was displayed.
#include "overlay.h"
#include <stdio.h>
#include <string.h>
struct Surface { void** vt; HDC dc; HBITMAP bitmap; HGDIOBJ old; void* pixels; LONG refs; };
struct Device { void** vt; LONG refs; };
static void* methods[119];
static void* surfaceMethods[17];
static Device d;
static Surface back, source;
static HRESULT fault, presentResult, resetResult;
static int faultStage, liveSources, updates, presentCalls, resetCalls;
static bool msaa;
static HANDLE entered, resume;
static bool blockPresent;
static StopOverlay stopDuringReset;
static ULONG WINAPI Add(Device* p) { return ++p->refs; }
static ULONG WINAPI Release(Device* p) { return --p->refs; }
static HRESULT WINAPI Creation(Device*,D3DDEVICE_CREATION_PARAMETERS* p) { *p={}; return S_OK; }
static HRESULT WINAPI Back(Device*,UINT,UINT,D3DBACKBUFFER_TYPE,IDirect3DSurface9** out) {
    if (faultStage==1) return fault;
    ++back.refs; *out=reinterpret_cast<IDirect3DSurface9*>(&back); return S_OK;
}
static HRESULT WINAPI Desc(Surface*,D3DSURFACE_DESC* p) {
    if (faultStage==2) return fault;
    *p={}; p->Width=800; p->Height=240; p->Format=D3DFMT_X8R8G8B8;
    p->MultiSampleType=msaa?D3DMULTISAMPLE_2_SAMPLES:D3DMULTISAMPLE_NONE; return S_OK;
}
static ULONG WINAPI SurfaceRelease(Surface* p) {
    LONG n=--p->refs;
    if (p==&source && !n) {
        SelectObject(p->dc,p->old); DeleteObject(p->bitmap); DeleteDC(p->dc); --liveSources;
    }
    return n;
}
static HRESULT WINAPI Offscreen(Device*,UINT w,UINT h,D3DFORMAT format,D3DPOOL pool,IDirect3DSurface9** out,HANDLE*) {
    if (faultStage==3) return fault;
    if (w!=580 || h!=32 || format!=D3DFMT_X8R8G8B8 || pool!=D3DPOOL_SYSTEMMEM) return E_INVALIDARG;
    source={}; source.vt=surfaceMethods; source.refs=1;
    source.dc=CreateCompatibleDC(nullptr);
    BITMAPINFO info={}; info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER); info.bmiHeader.biWidth=w;
    info.bmiHeader.biHeight=-static_cast<LONG>(h); info.bmiHeader.biPlanes=1; info.bmiHeader.biBitCount=32;
    source.bitmap=CreateDIBSection(source.dc,&info,DIB_RGB_COLORS,&source.pixels,nullptr,0);
    if (!source.bitmap || !source.dc) {
        if (source.bitmap) DeleteObject(source.bitmap);
        if (source.dc) DeleteDC(source.dc);
        return E_FAIL;
    }
    source.old=SelectObject(source.dc,source.bitmap); ++liveSources;
    *out=reinterpret_cast<IDirect3DSurface9*>(&source); return S_OK;
}
static HRESULT WINAPI SurfaceGetDC(Surface* p,HDC* out) { if (faultStage==4) return fault; *out=p->dc; return S_OK; }
static HRESULT WINAPI SurfaceReleaseDC(Surface*,HDC) { GdiFlush(); return faultStage==5?fault:S_OK; }
static int pixels;
static HRESULT WINAPI Update(Device*,IDirect3DSurface9* a,const RECT*,IDirect3DSurface9* b,const POINT* at) {
    if (faultStage==6) return fault;
    if (a!=reinterpret_cast<IDirect3DSurface9*>(&source)||b!=reinterpret_cast<IDirect3DSurface9*>(&back)||at->x!=10||at->y!=10) return E_INVALIDARG;
    pixels=0; DWORD* data=static_cast<DWORD*>(source.pixels);
    for (int i=0;i<580*32;i++) if ((data[i]&255)>180 && ((data[i]>>8)&255)>180 && ((data[i]>>16)&255)>180) pixels++;
    ++updates; return S_OK;
}
static HRESULT WINAPI Present(Device*,const RECT*,const RECT*,HWND,const RGNDATA*) {
    ++presentCalls;
    if (blockPresent) { SetEvent(entered); WaitForSingleObject(resume,INFINITE); }
    return presentResult;
}
static HRESULT WINAPI Reset(Device*,D3DPRESENT_PARAMETERS*) {
    ++resetCalls;
    if (stopDuringReset) stopDuringReset(FALSE);
    return resetResult;
}
static int checks,failed;
static void Check(bool ok,const char* label) { checks++; if (!ok) failed++; printf("%s %s\n",ok?"PASS":"FAIL",label); }
static IDirect3DDevice9* DevicePointer() { return reinterpret_cast<IDirect3DDevice9*>(&d); }
static DWORD WINAPI Worker(void*) { DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr); return 0; }
int wmain() {
    wchar_t path[MAX_PATH]; DWORD n=GetModuleFileNameW(nullptr,path,MAX_PATH);
    if (!n || n>MAX_PATH-32) return 2;
    wchar_t* slash=wcsrchr(path,L'\\'); if (!slash) return 2; wcscpy(slash+1,L"Ra3Overlay.dll");
    methods[1]=reinterpret_cast<void*>(&Add); methods[2]=reinterpret_cast<void*>(&Release);
    methods[9]=reinterpret_cast<void*>(&Creation); methods[16]=reinterpret_cast<void*>(&Reset);
    methods[17]=reinterpret_cast<void*>(&Present); methods[18]=reinterpret_cast<void*>(&Back);
    methods[30]=reinterpret_cast<void*>(&Update); methods[36]=reinterpret_cast<void*>(&Offscreen);
    surfaceMethods[2]=reinterpret_cast<void*>(&SurfaceRelease); surfaceMethods[12]=reinterpret_cast<void*>(&Desc);
    surfaceMethods[15]=reinterpret_cast<void*>(&SurfaceGetDC); surfaceMethods[16]=reinterpret_cast<void*>(&SurfaceReleaseDC);
    d={methods,1}; back.vt=surfaceMethods; back.refs=1; fault=E_FAIL;
    for (int cycle=0;cycle<20;cycle++) {
        HMODULE dll=LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        Check(dll!=nullptr,"load actual DLL"); if (!dll) return 2;
        auto start=reinterpret_cast<StartOverlay>(GetProcAddress(dll,"OverlayStart"));
        auto stop=reinterpret_cast<StopOverlay>(GetProcAddress(dll,"OverlayStop"));
        auto read=reinterpret_cast<ReadOverlay>(GetProcAddress(dll,"OverlayRead"));
        Check(start && stop && read,"ABI exports"); if (!start || !stop || !read) return 2;
        Check(start(DevicePointer()),"install function hooks"); Check(d.refs==2,"device retained");
        const int presentBefore=presentCalls, updateBefore=updates;
        DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr); OverlayStats s={}; read(&s);
        // 三个计数不只用来数数：跳板必须"恰好一次"穿透到原函数，多调一次就是真的双绘，
        // 这一条读出来才看得见（删掉计数会让 Present 的首指令变成 80 3D，直接装不上钩子）。
        Check(presentCalls==presentBefore+1,"hooked Present reaches the original exactly once");
        Check(updates==updateBefore+1,"hooked Present reaches the original drawing path once");
        Check(s.enabled && s.frames==1 && pixels>100,"real GDI glyphs reach synthetic UpdateSurface");
        Check(!stop(FALSE),"counter zero is not unload proof");
        const int presentMid=presentCalls;
        DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr); read(&s);
        Check(presentCalls==presentMid+1,"present after disable still reaches the original exactly once");
        Check(!s.enabled && s.frames==1,"disable prevents drawing");
        Check(stop(TRUE) && d.vt==methods && d.refs==1,"quiescent restore releases reference");
        Check(!liveSources && back.refs==1,"surfaces released");
        FreeLibrary(dll); Check(!GetModuleHandleW(L"Ra3Overlay.dll"),"actual DLL unload");
    }
    HMODULE dll=LoadLibraryExW(path,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
    if (!dll) return 2;
    auto start=reinterpret_cast<StartOverlay>(GetProcAddress(dll,"OverlayStart"));
    auto stop=reinterpret_cast<StopOverlay>(GetProcAddress(dll,"OverlayStop"));
    auto read=reinterpret_cast<ReadOverlay>(GetProcAddress(dll,"OverlayRead"));
    if (!start || !stop || !read) return 2;
    Check(!start(nullptr),"null device rejected");
    Check(start(DevicePointer()) && !start(DevicePointer()),"duplicate start rejected"); stop(TRUE);
    for (faultStage=1;faultStage<=6;faultStage++) {
        Check(start(DevicePointer()),"fault install"); DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr);
        OverlayStats s={}; read(&s); Check(!s.enabled && s.error==E_FAIL && !s.frames,"API failure closes drawing");
        Check(stop(TRUE) && !liveSources && back.refs==1,"fault cleanup");
    }
    faultStage=0; msaa=true; Check(start(DevicePointer()),"MSAA fixture install"); DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr);
    OverlayStats s={}; read(&s); Check(!s.enabled && s.error==D3DERR_NOTAVAILABLE,"MSAA refused"); stop(TRUE); msaa=false;
    Check(start(DevicePointer()),"Reset fixture install"); D3DPRESENT_PARAMETERS p={}; const int resetBefore=resetCalls; DevicePointer()->Reset(&p);
    Check(resetCalls==resetBefore+1,"hooked Reset reaches the original exactly once");
    DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr); read(&s);
    Check(s.enabled && s.frames==1 && s.resets==1,"successful Reset re-enables drawing"); stop(TRUE);
    Check(start(DevicePointer()),"stop-during-Reset fixture install"); stopDuringReset=stop; DevicePointer()->Reset(&p); stopDuringReset=nullptr;
    read(&s); Check(!s.enabled,"stop during Reset cannot be undone by successful Reset"); stop(TRUE);
    Check(start(DevicePointer()),"failed-Reset fixture install"); resetResult=D3DERR_DEVICELOST; Check(DevicePointer()->Reset(&p)==resetResult,"Reset HRESULT preserved");
    read(&s); Check(!s.enabled && s.error==resetResult,"failed Reset disables"); stop(TRUE); resetResult=S_OK;
    Check(start(DevicePointer()),"failed-Present fixture install"); presentResult=D3DERR_DEVICELOST;
    Check(DevicePointer()->Present(nullptr,nullptr,nullptr,nullptr)==presentResult,"Present HRESULT preserved");
    read(&s); Check(!s.enabled && s.error==presentResult,"Present failure disables"); stop(TRUE); presentResult=S_OK;
    Check(start(DevicePointer()),"foreign-owner fixture install"); void** ours=d.vt; void* foreign[119]; memcpy(foreign,ours,sizeof(foreign)); d.vt=foreign;
    Check(!stop(TRUE) && d.vt==foreign && d.refs==2,"foreign owner retained not overwritten"); d.vt=ours;
    Check(stop(TRUE),"owner restored permits detach");
    Check(start(DevicePointer()),"threaded fixture install"); entered=CreateEventW(nullptr,TRUE,FALSE,nullptr); resume=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    blockPresent=true; HANDLE worker=CreateThread(nullptr,0,Worker,nullptr,0,nullptr);
    if (!entered || !resume || !worker) return 2;
    Check(WaitForSingleObject(entered,3000)==WAIT_OBJECT_0,"in-flight callback entered");
    Check(!stop(TRUE),"in-flight callback refuses even claimed quiescence");
    SetEvent(resume); WaitForSingleObject(worker,INFINITE); CloseHandle(worker); CloseHandle(entered); CloseHandle(resume); blockPresent=false;
    read(&s); Check(!s.enabled && s.error==D3DERR_INVALIDCALL,"foreign thread fails closed");
    Check(stop(TRUE) && !liveSources && d.refs==1,"joined worker safe detach");
    FreeLibrary(dll);
    printf("RESULT synthetic-COM checks=%d failures=%d GDI-pixels=%d\n",checks,failed,pixels);
    return failed?1:0;
}
