#pragma once
#include <windows.h>
#include <d3d9.h>
#include <stdio.h>
#include <string.h>

// Shared by the product and contract fixture. No device/vtable writes.
namespace TextureOsd {
using Sink = void (*)(const char*);
struct Trace {
    Sink sink; bool verbose; const char* failed=nullptr; const char* prefix="osd-draw";
    void Line(const char* line) const noexcept { if(sink) sink(line); }
    HRESULT Step(const char* name,HRESULT result) noexcept {
        if(FAILED(result) && !failed) failed=name;
        if(verbose || FAILED(result)) {
            char line[192]; snprintf(line,sizeof(line),"%s-step name=%s hr=%08lx",prefix,name,static_cast<unsigned long>(result)); Line(line);
        }
        return result;
    }
    void Desc(const char* name,const D3DSURFACE_DESC& d) const noexcept {
        if(!verbose) return;
        char line[256]; snprintf(line,sizeof(line),"%s-desc name=%s Format=%u Pool=%u MultiSampleType=%u MultiSampleQuality=%lu Usage=%lu Width=%u Height=%u",
            prefix,name,d.Format,d.Pool,d.MultiSampleType,d.MultiSampleQuality,d.Usage,d.Width,d.Height); Line(line);
    }
};
template<class T> struct Ref {
    T* p=nullptr;
    ~Ref() { if(p) p->Release(); }
    T* operator->() const { return p; }
};
inline void Restrictions(Trace& t,const D3DSURFACE_DESC& s,const D3DSURFACE_DESC& b) noexcept {
    if(!t.verbose) return;
    const bool pool=s.Pool!=D3DPOOL_SYSTEMMEM || b.Pool!=D3DPOOL_DEFAULT;
    const bool format=s.Format!=b.Format;
    const bool msaa=s.MultiSampleType!=D3DMULTISAMPLE_NONE || b.MultiSampleType!=D3DMULTISAMPLE_NONE;
    const bool depth=(s.Usage&D3DUSAGE_DEPTHSTENCIL) || (b.Usage&D3DUSAGE_DEPTHSTENCIL);
    const bool bounds=b.Width<590 || b.Height<42;
    char line[384]; snprintf(line,sizeof(line),"osd-draw-legacy-restrictions pool=%u format=%u multisample=%u depth=%u bounds=%u scaling=0 sourceRect=0,0,580,32 targetPoint=10,10 sourceLocked=0 dcHeld=0 conclusion=%s UpdateSurface=not-called",
        pool,format,msaa,depth,bounds,(pool||format||msaa||depth||bounds)?"violated-listed-restriction":"no-listed-violation-cause-unproven"); t.Line(line);
}
inline HRESULT Diagnostics(IDirect3DDevice9* d,Trace& t) noexcept {
    Ref<IDirect3DSwapChain9> chain; Ref<IDirect3D9> api;
    D3DPRESENT_PARAMETERS p={}; D3DDEVICE_CREATION_PARAMETERS cp={};
    const HRESULT swap=t.Step("GetSwapChain",d->GetSwapChain(0,&chain.p)); if(FAILED(swap)) return swap;
    const HRESULT params=t.Step("GetPresentParameters",chain->GetPresentParameters(&p)); if(FAILED(params)) return params;
    if(t.verbose) {
        char line[256]; snprintf(line,sizeof(line),"osd-draw-present BackBufferFormat=%u MultiSampleType=%u MultiSampleQuality=%lu SwapEffect=%u Width=%u Height=%u Windowed=%u",
            p.BackBufferFormat,p.MultiSampleType,p.MultiSampleQuality,p.SwapEffect,p.BackBufferWidth,p.BackBufferHeight,p.Windowed); t.Line(line);
    }
    const HRESULT creation=t.Step("GetCreationParameters",d->GetCreationParameters(&cp)); if(FAILED(creation)) return creation;
    const HRESULT getApi=t.Step("GetDirect3D",d->GetDirect3D(&api.p)); if(FAILED(getApi)) return getApi;
    DWORD levels=0;
    const HRESULT sample=t.Step("CheckDeviceMultiSampleType",api->CheckDeviceMultiSampleType(cp.AdapterOrdinal,cp.DeviceType,p.BackBufferFormat,p.Windowed,p.MultiSampleType,&levels));
    if(t.verbose || FAILED(sample)) { char line[192]; snprintf(line,sizeof(line),"osd-draw-msaa adapter=%u deviceType=%u format=%u windowed=%u type=%u quality=%lu qualityLevels=%lu hr=%08lx",
        cp.AdapterOrdinal,cp.DeviceType,p.BackBufferFormat,p.Windowed,p.MultiSampleType,p.MultiSampleQuality,levels,static_cast<unsigned long>(sample)); t.Line(line); }
    return sample;
}
inline HRESULT Raster(IDirect3DSurface9* source,const WCHAR* text,Trace& t) noexcept {
    HDC dc=nullptr;
    const HRESULT get=t.Step("GetSourceDC",source->GetDC(&dc)); if(FAILED(get)) return get;
    RECT rect={0,0,580,32}; HBRUSH brush=CreateSolidBrush(RGB(20,24,32));
    HFONT font=CreateFontW(-20,0,0,0,FW_NORMAL,FALSE,FALSE,FALSE,DEFAULT_CHARSET,OUT_DEFAULT_PRECIS,CLIP_DEFAULT_PRECIS,ANTIALIASED_QUALITY,DEFAULT_PITCH,L"Microsoft YaHei");
    HGDIOBJ old=font?SelectObject(dc,font):nullptr;
    BOOL ok=brush && font && old && old!=HGDI_ERROR;
    if(ok) ok=FillRect(dc,&rect,brush)!=0 && SetBkMode(dc,TRANSPARENT)!=0 && SetTextColor(dc,RGB(240,244,250))!=CLR_INVALID &&
        DrawTextW(dc,text,-1,&rect,DT_SINGLELINE|DT_VCENTER|DT_LEFT|DT_NOPREFIX)!=0;
    if(old && old!=HGDI_ERROR) SelectObject(dc,old);
    if(font) DeleteObject(font);
    if(brush) DeleteObject(brush);
    const HRESULT raster=t.Step("GDI-Text",ok?S_OK:E_FAIL);
    const HRESULT release=t.Step("ReleaseSourceDC",source->ReleaseDC(dc));
    return FAILED(raster)?raster:release;
}
inline HRESULT Upload(IDirect3DSurface9* source,IDirect3DTexture9* texture,Trace& t) noexcept {
    D3DLOCKED_RECT from={},to={};
    const HRESULT read=t.Step("LockSource",source->LockRect(&from,nullptr,D3DLOCK_READONLY)); if(FAILED(read)) return read;
    const HRESULT write=t.Step("LockTexture",texture->LockRect(0,&to,nullptr,0));
    if(SUCCEEDED(write)) {
        for(UINT y=0;y<32;++y) {
            const DWORD* row=reinterpret_cast<const DWORD*>(static_cast<const BYTE*>(from.pBits)+y*from.Pitch);
            DWORD* dest=reinterpret_cast<DWORD*>(static_cast<BYTE*>(to.pBits)+y*to.Pitch);
            for(UINT x=0;x<1024;++x) dest[x]=x<580?(row[x]|0xff000000):0xff141820;
        }
    }
    const HRESULT unlockRead=t.Step("UnlockSource",source->UnlockRect());
    const HRESULT unlockWrite=SUCCEEDED(write)?t.Step("UnlockTexture",texture->UnlockRect(0)):S_OK;
    if(FAILED(write)) return write;
    return FAILED(unlockRead)?unlockRead:unlockWrite;
}

// D3DSBT_ALL excludes render targets / depth surface. Save them explicitly;
// restore those first because SetRenderTarget also changes the viewport.
struct Saved {
    IDirect3DDevice9* d; Trace& t; Ref<IDirect3DStateBlock9> block;
    Ref<IDirect3DSurface9> targets[4],depth;
    UINT count=0; bool captured=false,changed=false,scene=false;
    HRESULT Capture(const D3DCAPS9& caps) noexcept {
        count=caps.NumSimultaneousRTs<4?caps.NumSimultaneousRTs:4;
        if(!count) return t.Step("RenderTargetCaps",D3DERR_NOTAVAILABLE);
        const HRESULT create=t.Step("CreateStateBlock",d->CreateStateBlock(D3DSBT_ALL,&block.p)); if(FAILED(create)) return create;
        const HRESULT capture=t.Step("CaptureStateBlock",block->Capture()); if(FAILED(capture)) return capture;
        for(UINT i=0;i<count;++i) {
            char name[64]; snprintf(name,sizeof(name),"GetRenderTarget%u",i);
            const HRESULT hr=d->GetRenderTarget(i,&targets[i].p);
            // NOTFOUND denotes an unbound optional slot, not a draw failure.
            if(i && hr==D3DERR_NOTFOUND) { if(t.verbose) {char line[128];snprintf(line,sizeof(line),"osd-draw-step name=%s hr=%08lx optional=unbound",name,static_cast<unsigned long>(hr));t.Line(line);} }
            else if(FAILED(t.Step(name,hr))) return hr;
        }
        const HRESULT dep=d->GetDepthStencilSurface(&depth.p);
        if(dep==D3DERR_NOTFOUND) { if(t.verbose) t.Line("osd-draw-step name=GetDepthStencilSurface hr=88760866 optional=unbound"); }
        else if(FAILED(t.Step("GetDepthStencilSurface",dep))) return dep;
        captured=true; return S_OK;
    }
    HRESULT Restore() noexcept {
        HRESULT result=S_OK;
        auto record=[&](const char* name,HRESULT hr) { t.Step(name,hr); if(FAILED(hr) && SUCCEEDED(result)) result=hr; };
        if(scene) { record("EndScene",d->EndScene()); scene=false; }
        if(captured && changed) {
            record("RestoreDepth-unbind",d->SetDepthStencilSurface(nullptr));
            for(UINT i=1;i<count;++i) {char name[64];snprintf(name,sizeof(name),"RestoreRT-unbind%u",i);record(name,d->SetRenderTarget(i,nullptr));}
            for(UINT i=0;i<count;++i) {char name[64];snprintf(name,sizeof(name),"RestoreRenderTarget%u",i);record(name,d->SetRenderTarget(i,targets[i].p));}
            record("RestoreDepth",d->SetDepthStencilSurface(depth.p));
            record("ApplyStateBlock",block->Apply());
            changed=false;
        }
        return result;
    }
};
inline HRESULT Draw(IDirect3DDevice9* d,const WCHAR* text,Trace& t) noexcept {
    Ref<IDirect3DSurface9> back,source; Ref<IDirect3DTexture9> texture;
    D3DSURFACE_DESC desc={},src={}; D3DCAPS9 caps={};
#define OSD_TRY(name,call) do { const HRESULT stepResult=t.Step(name,(call)); if(FAILED(stepResult)) return stepResult; } while(0)
    OSD_TRY("GetBackBuffer",d->GetBackBuffer(0,0,D3DBACKBUFFER_TYPE_MONO,&back.p));
    OSD_TRY("GetBackBufferDesc",back->GetDesc(&desc)); t.Desc("target",desc);
    OSD_TRY("CreateSourceSurface",d->CreateOffscreenPlainSurface(580,32,D3DFMT_X8R8G8B8,D3DPOOL_SYSTEMMEM,&source.p,nullptr));
    OSD_TRY("GetSourceDesc",source->GetDesc(&src)); t.Desc("source",src);
    Restrictions(t,src,desc);
    const HRESULT diagnostics=Diagnostics(d,t); if(FAILED(diagnostics)) return diagnostics;
    OSD_TRY("GetDeviceCaps",d->GetDeviceCaps(&caps));
    OSD_TRY("TargetBounds",desc.Width>=590 && desc.Height>=42?S_OK:D3DERR_NOTAVAILABLE);
    const HRESULT raster=Raster(source.p,text,t); if(FAILED(raster)) return raster;
    // Power-of-two allocation works on devices without unconditional NPOT support.
    OSD_TRY("CreateTexture",d->CreateTexture(1024,32,1,0,D3DFMT_A8R8G8B8,D3DPOOL_MANAGED,&texture.p,nullptr));
    const HRESULT upload=Upload(source.p,texture.p,t); if(FAILED(upload)) return upload;
    Saved saved{d,t,{},{},{}};
    const HRESULT capture=saved.Capture(caps); if(FAILED(capture)) return capture;
    const HRESULT begin=t.Step("BeginScene",d->BeginScene()); if(FAILED(begin)) return begin;
    saved.scene=true; saved.changed=true;
    // Every early return below is inside the lambda; cleanup always runs afterwards.
    const HRESULT draw=[&]() noexcept -> HRESULT {
        OSD_TRY("SetDepthStencilSurface",d->SetDepthStencilSurface(nullptr));
        for(UINT i=1;i<saved.count;++i) {char name[64];snprintf(name,sizeof(name),"SetRenderTarget%u-null",i);OSD_TRY(name,d->SetRenderTarget(i,nullptr));}
        OSD_TRY("SetRenderTarget0",d->SetRenderTarget(0,back.p));
        D3DVIEWPORT9 viewport={0,0,desc.Width,desc.Height,0,1};
        OSD_TRY("SetViewport",d->SetViewport(&viewport));
        OSD_TRY("SetVertexShader",d->SetVertexShader(nullptr));
        OSD_TRY("SetPixelShader",d->SetPixelShader(nullptr));
        OSD_TRY("SetFVF",d->SetFVF(D3DFVF_XYZRHW|D3DFVF_TEX1));
        for(UINT i=0;i<caps.MaxStreams;++i) {char name[64];snprintf(name,sizeof(name),"SetStreamSourceFreq%u",i);OSD_TRY(name,d->SetStreamSourceFreq(i,1));}
        struct State {D3DRENDERSTATETYPE key;DWORD value;};
        const State states[]={{D3DRS_ZENABLE,FALSE},{D3DRS_ZWRITEENABLE,FALSE},{D3DRS_ALPHATESTENABLE,FALSE},{D3DRS_ALPHABLENDENABLE,FALSE},
            {D3DRS_SEPARATEALPHABLENDENABLE,FALSE},{D3DRS_STENCILENABLE,FALSE},{D3DRS_CULLMODE,D3DCULL_NONE},{D3DRS_FILLMODE,D3DFILL_SOLID},
            {D3DRS_SHADEMODE,D3DSHADE_GOURAUD},{D3DRS_FOGENABLE,FALSE},{D3DRS_LIGHTING,FALSE},{D3DRS_SPECULARENABLE,FALSE},
            {D3DRS_CLIPPLANEENABLE,0},{D3DRS_SCISSORTESTENABLE,FALSE},{D3DRS_COLORWRITEENABLE,15},{D3DRS_SRGBWRITEENABLE,FALSE},
            {D3DRS_MULTISAMPLEANTIALIAS,TRUE},{D3DRS_MULTISAMPLEMASK,0xffffffff},{D3DRS_WRAP0,0}};
        for(const auto& state:states) {char name[64];snprintf(name,sizeof(name),"SetRenderState-%u",state.key);OSD_TRY(name,d->SetRenderState(state.key,state.value));}
        OSD_TRY("SetTexture",d->SetTexture(0,texture.p));
        const struct {D3DTEXTURESTAGESTATETYPE key;DWORD value;} stages[]={
            {D3DTSS_COLOROP,D3DTOP_SELECTARG1},{D3DTSS_COLORARG1,D3DTA_TEXTURE},{D3DTSS_ALPHAOP,D3DTOP_SELECTARG1},{D3DTSS_ALPHAARG1,D3DTA_TEXTURE},
            {D3DTSS_TEXCOORDINDEX,0},{D3DTSS_TEXTURETRANSFORMFLAGS,D3DTTFF_DISABLE},{D3DTSS_RESULTARG,D3DTA_CURRENT}};
        for(const auto& stage:stages) {char name[64];snprintf(name,sizeof(name),"SetTextureStageState0-%u",stage.key);OSD_TRY(name,d->SetTextureStageState(0,stage.key,stage.value));}
        OSD_TRY("DisableTextureStage1-color",d->SetTextureStageState(1,D3DTSS_COLOROP,D3DTOP_DISABLE));
        OSD_TRY("DisableTextureStage1-alpha",d->SetTextureStageState(1,D3DTSS_ALPHAOP,D3DTOP_DISABLE));
        const struct {D3DSAMPLERSTATETYPE key;DWORD value;} samplers[]={{D3DSAMP_MINFILTER,D3DTEXF_POINT},{D3DSAMP_MAGFILTER,D3DTEXF_POINT},
            {D3DSAMP_MIPFILTER,D3DTEXF_NONE},{D3DSAMP_ADDRESSU,D3DTADDRESS_CLAMP},{D3DSAMP_ADDRESSV,D3DTADDRESS_CLAMP},{D3DSAMP_SRGBTEXTURE,0}};
        for(const auto& sampler:samplers) {char name[64];snprintf(name,sizeof(name),"SetSamplerState0-%u",sampler.key);OSD_TRY(name,d->SetSamplerState(0,sampler.key,sampler.value));}
        struct Vertex {float x,y,z,rhw,u,v;};
        const float u=580.0f/1024;
        const Vertex vertices[]={{9.5f,9.5f,0,1,0,0},{589.5f,9.5f,0,1,u,0},{9.5f,41.5f,0,1,0,1},
            {9.5f,41.5f,0,1,0,1},{589.5f,9.5f,0,1,u,0},{589.5f,41.5f,0,1,u,1}};
        OSD_TRY("DrawPrimitiveUP",d->DrawPrimitiveUP(D3DPT_TRIANGLELIST,2,vertices,sizeof(Vertex)));
        return S_OK;
    }();
    const HRESULT restore=saved.Restore();
#undef OSD_TRY
    return FAILED(draw)?draw:restore;
}
}
