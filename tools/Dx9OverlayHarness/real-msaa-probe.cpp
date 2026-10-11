#include <windows.h>
#include <d3d9.h>
#include <stdio.h>
#include "texture-draw.h"
static int failures, checks;
static void Check(bool ok,const char* text) { ++checks; if(!ok) ++failures; printf("%s %s\n",ok?"PASS":"FAIL",text); }
int wmain() {
    setvbuf(stdout,nullptr,_IONBF,0);
    HWND window=CreateWindowExW(WS_EX_NOACTIVATE,L"STATIC",L"Round34 hidden fixture",WS_POPUP,0,0,800,240,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    IDirect3D9* api=Direct3DCreate9(D3D_SDK_VERSION);
    if(!api || !window) return 2;
    DWORD session=0;ProcessIdToSessionId(GetCurrentProcessId(),&session);
    printf("EVIDENCE real D3D9 probe, not game; session=%lu adapters=%u\n",session,api->GetAdapterCount());
    D3DPRESENT_PARAMETERS p={}; p.Windowed=TRUE; p.SwapEffect=D3DSWAPEFFECT_DISCARD; p.hDeviceWindow=window;
    p.BackBufferWidth=800; p.BackBufferHeight=240; p.BackBufferFormat=D3DFMT_X8R8G8B8;
    p.PresentationInterval=D3DPRESENT_INTERVAL_IMMEDIATE;
    DWORD quality=0;
    for(UINT sample=2;sample<=16;++sample) {
        HRESULT hr=api->CheckDeviceMultiSampleType(0,D3DDEVTYPE_HAL,p.BackBufferFormat,TRUE,static_cast<D3DMULTISAMPLE_TYPE>(sample),&quality);
        printf("fixture CheckDeviceMultiSampleType type=%u hr=%08lx qualityLevels=%lu\n",sample,static_cast<unsigned long>(hr),quality);
        if(SUCCEEDED(hr) && quality) {p.MultiSampleType=static_cast<D3DMULTISAMPLE_TYPE>(sample);break;}
    }
    Check(p.MultiSampleType!=D3DMULTISAMPLE_NONE,"real MSAA support (no skip/fallback to non-MSAA)");
    if(!p.MultiSampleType) return 2;
    IDirect3DDevice9* d=nullptr;
    HRESULT create=api->CreateDevice(0,D3DDEVTYPE_HAL,window,D3DCREATE_SOFTWARE_VERTEXPROCESSING,&p,&d);
    printf("fixture CreateDevice hr=%08lx\n",static_cast<unsigned long>(create));
    if(FAILED(create)) return 2;
    IDirect3DSurface9 *back=nullptr,*source=nullptr;
    // Check 只记一笔失败就继续，而下一行要解引用 back：必须在这里就断掉，
    // 否则失败路径变成空指针崩溃（探针本来是"取证用的"，不能自己崩掉把现场弄糊）。
    Check(SUCCEEDED(d->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back)) && back!=nullptr,"GetBackBuffer");
    if(!back) {
        printf("fixture no back buffer, stopping before desc read\n");
        d->Release(); api->Release(); DestroyWindow(window);
        printf("RESULT real-device-probe checks=%d failures=%d skipped=0\n",checks,failures);
        return 2;
    }
    D3DSURFACE_DESC desc={}; back->GetDesc(&desc);
    printf("fixture target Format=%u Pool=%u MultiSampleType=%u Quality=%lu Usage=%lu Width=%u Height=%u\n",desc.Format,desc.Pool,desc.MultiSampleType,desc.MultiSampleQuality,desc.Usage,desc.Width,desc.Height);
    Check(SUCCEEDED(d->CreateOffscreenPlainSurface(580,32,desc.Format,D3DPOOL_SYSTEMMEM,&source,nullptr)),"CreateSourceSurface same format SYSTEMMEM");
    POINT offset={10,10}; HRESULT legacy=d->UpdateSurface(source,nullptr,back,&offset);
    printf("osd-draw-step name=UpdateSurface hr=%08lx fixture=legacy-api\n",static_cast<unsigned long>(legacy));
    Check(legacy==D3DERR_INVALIDCALL,"legacy surface copy fails D3DERR_INVALIDCALL on MSAA");
    TextureOsd::Trace trace{[](const char* line){puts(line);},true};
    Check(TextureOsd::Draw(d,L"RA3 real-device probe",trace)==S_OK,"shared new texture renderer succeeds on same real device");
    source->Release(); back->Release(); d->Release(); api->Release(); DestroyWindow(window);
    printf("RESULT real-device-probe checks=%d failures=%d skipped=0\n",checks,failures);
    return failures?1:0;
}
