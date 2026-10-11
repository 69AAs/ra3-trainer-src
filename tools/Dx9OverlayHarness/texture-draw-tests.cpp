#include "texture-draw.h"
#include <vector>
#include <string>
#include <array>
#include <stdlib.h>
using namespace TextureOsd;
static int checks,failures,calls,failAt=-1,draws,updates;
static bool scene;
static std::vector<std::string> names;
static void Check(bool ok,const char* text) {++checks;if(!ok)++failures;printf("%s %s\n",ok?"PASS":"FAIL",text);}
static bool Fault(const char* name) {names.push_back(name);return ++calls==failAt;}
#define CALL(name) if(Fault(name)) return E_FAIL
struct Object {void** vt;LONG refs;};
struct Surface {void** vt;LONG refs;D3DSURFACE_DESC desc;HDC dc;HBITMAP bitmap;HGDIOBJ old;void* bits;bool locked,held;};
struct Stream {void* buffer;UINT offset,stride,freq;};
struct State {
    DWORD render[256],stage[8][33],sampler[16][14];
    void* textures[16]; Stream streams[16];
    void *decl,*vs,*ps,*indices; DWORD fvf;
    D3DVIEWPORT9 viewport; RECT scissor;
    float transforms[266][16],clip[6][4],vsConstants[256][4],psConstants[224][4];
    D3DMATERIAL9 material;
    BOOL lightEnabled[8]; D3DLIGHT9 lights[8];
    int vsInt[16][4],psInt[16][4]; BOOL vsBool[16],psBool[16];
    void* vertexTextures[4]; UINT palette;
};
static State state,snapshot;
static Surface back,source,hostRT[4],depth;
static Surface* targets[4]; static Surface* currentDepth;
static void *dv[119],*sv[17],*tv[22],*bv[6],*av[17],*cv[10];
static Object device{dv,1},texture{tv,0},block{bv,0},api{av,1},chain{cv,1};
static DWORD texturePixels[32][1040];
static D3DPRESENT_PARAMETERS present;
static ULONG WINAPI Add(Object* p) {return ++p->refs;}
static ULONG WINAPI Release(Object* p) {return --p->refs;}
static ULONG WINAPI SurfaceRelease(Surface* p) {
    LONG n=--p->refs;
    if(p==&source && !n && p->dc) {SelectObject(p->dc,p->old);DeleteObject(p->bitmap);DeleteDC(p->dc);p->dc=nullptr;}
    return n;
}
static HRESULT WINAPI Back(Object*,UINT,UINT,D3DBACKBUFFER_TYPE,IDirect3DSurface9** out) {CALL("GetBackBuffer");++back.refs;*out=reinterpret_cast<IDirect3DSurface9*>(&back);return S_OK;}
static HRESULT WINAPI Desc(Surface* p,D3DSURFACE_DESC* out) {CALL(p==&back?"GetBackBufferDesc":"GetSourceDesc");*out=p->desc;return S_OK;}
static HRESULT WINAPI Offscreen(Object*,UINT w,UINT h,D3DFORMAT format,D3DPOOL pool,IDirect3DSurface9** out,HANDLE*) {
    CALL("CreateSourceSurface");source={};source.vt=sv;source.refs=1;
    source.desc={format,D3DRTYPE_SURFACE,0,pool,D3DMULTISAMPLE_NONE,0,w,h};
    BITMAPINFO info={};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=w;info.bmiHeader.biHeight=-static_cast<LONG>(h);
    info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;info.bmiHeader.biCompression=BI_RGB;
    source.dc=CreateCompatibleDC(nullptr);source.bitmap=CreateDIBSection(source.dc,&info,DIB_RGB_COLORS,&source.bits,nullptr,0);
    if(!source.dc || !source.bitmap) {
        // Release whatever was created before failing, so the fixture never leaks a DC/bitmap.
        if(source.bitmap) DeleteObject(source.bitmap);
        if(source.dc) DeleteDC(source.dc);
        source.bitmap=nullptr; source.dc=nullptr;
        return E_FAIL;
    }
    source.old=SelectObject(source.dc,source.bitmap);*out=reinterpret_cast<IDirect3DSurface9*>(&source);return S_OK;
}
static HRESULT WINAPI SourceDC(Surface* p,HDC* out) {CALL("GetSourceDC");p->held=true;*out=p->dc;return S_OK;}
static HRESULT WINAPI SourceReleaseDC(Surface* p,HDC) {p->held=false;CALL("ReleaseSourceDC");return S_OK;}
static HRESULT WINAPI LockSource(Surface* p,D3DLOCKED_RECT* out,const RECT*,DWORD) {CALL("LockSource");p->locked=true;*out={580*4,p->bits};return S_OK;}
static HRESULT WINAPI UnlockSource(Surface* p) {p->locked=false;CALL("UnlockSource");return S_OK;}
static HRESULT WINAPI Swap(Object*,UINT,IDirect3DSwapChain9** out) {CALL("GetSwapChain");++chain.refs;*out=reinterpret_cast<IDirect3DSwapChain9*>(&chain);return S_OK;}
static HRESULT WINAPI Params(Object*,D3DPRESENT_PARAMETERS* out) {CALL("GetPresentParameters");*out=present;return S_OK;}
static HRESULT WINAPI Creation(Object*,D3DDEVICE_CREATION_PARAMETERS* out) {CALL("GetCreationParameters");*out={0,D3DDEVTYPE_HAL,nullptr,D3DCREATE_SOFTWARE_VERTEXPROCESSING};return S_OK;}
static HRESULT WINAPI Api(Object*,IDirect3D9** out) {CALL("GetDirect3D");++api.refs;*out=reinterpret_cast<IDirect3D9*>(&api);return S_OK;}
static HRESULT WINAPI Msaa(Object*,UINT,D3DDEVTYPE,D3DFORMAT,BOOL,D3DMULTISAMPLE_TYPE,DWORD* quality) {CALL("CheckDeviceMultiSampleType");*quality=1;return S_OK;}
static HRESULT WINAPI Caps(Object*,D3DCAPS9* out) {CALL("GetDeviceCaps");*out={};out->NumSimultaneousRTs=4;out->MaxStreams=16;return S_OK;}
static HRESULT WINAPI Texture(Object*,UINT w,UINT h,UINT levels,DWORD usage,D3DFORMAT fmt,D3DPOOL pool,IDirect3DTexture9** out,HANDLE*) {
    CALL("CreateTexture");if(w!=1024||h!=32||levels!=1||usage||fmt!=D3DFMT_A8R8G8B8||pool!=D3DPOOL_MANAGED) return D3DERR_INVALIDCALL;
    ++texture.refs;*out=reinterpret_cast<IDirect3DTexture9*>(&texture);return S_OK;
}
static HRESULT WINAPI LockTexture(Object*,UINT,D3DLOCKED_RECT* out,const RECT*,DWORD) {CALL("LockTexture");*out={sizeof(texturePixels[0]),texturePixels};return S_OK;}
static HRESULT WINAPI UnlockTexture(Object*,UINT) {CALL("UnlockTexture");return S_OK;}
static HRESULT WINAPI StateBlock(Object*,D3DSTATEBLOCKTYPE type,IDirect3DStateBlock9** out) {
    CALL("CreateStateBlock");if(type!=D3DSBT_ALL)return E_INVALIDARG;++block.refs;snapshot=state;*out=reinterpret_cast<IDirect3DStateBlock9*>(&block);return S_OK;
}
static HRESULT WINAPI Capture(Object*) {CALL("CaptureStateBlock");snapshot=state;return S_OK;}
static HRESULT WINAPI Apply(Object*) {CALL("ApplyStateBlock");state=snapshot;return S_OK;}
static HRESULT WINAPI GetRT(Object*,DWORD index,IDirect3DSurface9** out) {CALL("GetRenderTarget");*out=reinterpret_cast<IDirect3DSurface9*>(targets[index]);if(!*out)return D3DERR_NOTFOUND;++targets[index]->refs;return S_OK;}
static HRESULT WINAPI GetDepth(Object*,IDirect3DSurface9** out) {CALL("GetDepthStencilSurface");*out=reinterpret_cast<IDirect3DSurface9*>(currentDepth);if(!*out)return D3DERR_NOTFOUND;++currentDepth->refs;return S_OK;}
static HRESULT WINAPI SetRT(Object*,DWORD index,IDirect3DSurface9* value) {CALL("SetRenderTarget");targets[index]=reinterpret_cast<Surface*>(value);if(!index && value) state.viewport={0,0,targets[0]->desc.Width,targets[0]->desc.Height,0,1};return S_OK;}
static HRESULT WINAPI SetDepth(Object*,IDirect3DSurface9* value) {CALL("SetDepthStencilSurface");currentDepth=reinterpret_cast<Surface*>(value);return S_OK;}
static HRESULT WINAPI Begin(Object*) {CALL("BeginScene");if(scene)return D3DERR_INVALIDCALL;scene=true;return S_OK;}
static HRESULT WINAPI End(Object*) {scene=false;CALL("EndScene");return S_OK;}
static HRESULT WINAPI Viewport(Object*,const D3DVIEWPORT9* value) {CALL("SetViewport");state.viewport=*value;return S_OK;}
static HRESULT WINAPI VS(Object*,IDirect3DVertexShader9* value) {CALL("SetVertexShader");state.vs=value;return S_OK;}
static HRESULT WINAPI PS(Object*,IDirect3DPixelShader9* value) {CALL("SetPixelShader");state.ps=value;return S_OK;}
static HRESULT WINAPI FVF(Object*,DWORD value) {CALL("SetFVF");state.fvf=value;state.decl=reinterpret_cast<void*>(0xabcdef);return S_OK;}
static HRESULT WINAPI Frequency(Object*,UINT index,UINT value) {CALL("SetStreamSourceFreq");state.streams[index].freq=value;return S_OK;}
static HRESULT WINAPI Render(Object*,D3DRENDERSTATETYPE key,DWORD value) {CALL("SetRenderState");state.render[key]=value;return S_OK;}
static HRESULT WINAPI Bind(Object*,DWORD index,IDirect3DBaseTexture9* value) {CALL("SetTexture");state.textures[index]=value;return S_OK;}
static HRESULT WINAPI Stage(Object*,DWORD index,D3DTEXTURESTAGESTATETYPE key,DWORD value) {CALL("SetTextureStageState");state.stage[index][key]=value;return S_OK;}
static HRESULT WINAPI Sampler(Object*,DWORD index,D3DSAMPLERSTATETYPE key,DWORD value) {CALL("SetSamplerState");state.sampler[index][key]=value;return S_OK;}
static HRESULT WINAPI DrawUP(Object*,D3DPRIMITIVETYPE type,UINT count,const void* vertices,UINT stride) {
    CALL("DrawPrimitiveUP");++draws;
    Check(scene && targets[0]==&back && !currentDepth && !targets[1] && !targets[2] && !targets[3],"quad draws inside scene to actual MSAA backbuffer without foreign targets/depth");
    Check(type==D3DPT_TRIANGLELIST && count==2 && stride==24 && state.fvf==(D3DFVF_XYZRHW|D3DFVF_TEX1),"two screen-space textured triangles with correct FVF/stride");
    const float* v=static_cast<const float*>(vertices);
    Check(v[0]==9.5f && v[1]==9.5f && v[6]==589.5f && v[12+1]==41.5f && v[10]==580.0f/1024,"quad pixel-center and UV coordinates");
    Check(!state.vs && !state.ps && state.textures[0]==&texture && !state.render[D3DRS_ZENABLE] && state.render[D3DRS_COLORWRITEENABLE]==15 && !state.render[D3DRS_SCISSORTESTENABLE],"host shaders/scissor/depth cannot suppress overlay");
    Check(state.stage[0][D3DTSS_COLOROP]==D3DTOP_SELECTARG1 && state.stage[1][D3DTSS_COLOROP]==D3DTOP_DISABLE && state.sampler[0][D3DSAMP_MINFILTER]==D3DTEXF_POINT,"texture stages and samplers configured");
    bool ink=false,alpha=true;for(UINT y=0;y<32;++y)for(UINT x=0;x<580;++x){alpha=alpha && (texturePixels[y][x]>>24)==255;ink=ink || (texturePixels[y][x]&0xffffff)!=0x141820;}
    Check(ink && alpha,"real GDI Chinese text uploaded with pitch and opaque alpha");
    state.streams[0].buffer=nullptr;state.streams[0].offset=0;state.streams[0].stride=0;return S_OK;
}
static HRESULT WINAPI Update(Object*,IDirect3DSurface9* s,const RECT*,IDirect3DSurface9* b,const POINT*) {
    ++updates;auto a=reinterpret_cast<Surface*>(s);auto z=reinterpret_cast<Surface*>(b);
    if(a->desc.Pool!=D3DPOOL_SYSTEMMEM||z->desc.Pool!=D3DPOOL_DEFAULT||a->desc.Format!=z->desc.Format||a->desc.MultiSampleType||z->desc.MultiSampleType||a->locked||a->held)return D3DERR_INVALIDCALL;
    return S_OK;
}
static void Initialize() {
#define SLOT(table,index,fn) table[index]=reinterpret_cast<void*>(&fn)
    for(auto table:{dv,sv,tv,bv,av,cv}) {SLOT(table,1,Add);SLOT(table,2,Release);}
    SLOT(sv,2,SurfaceRelease);SLOT(sv,12,Desc);SLOT(sv,13,LockSource);SLOT(sv,14,UnlockSource);SLOT(sv,15,SourceDC);SLOT(sv,16,SourceReleaseDC);
    SLOT(dv,6,Api);SLOT(dv,7,Caps);SLOT(dv,9,Creation);SLOT(dv,14,Swap);SLOT(dv,18,Back);SLOT(dv,23,Texture);SLOT(dv,30,Update);SLOT(dv,36,Offscreen);
    SLOT(dv,37,SetRT);SLOT(dv,38,GetRT);SLOT(dv,39,SetDepth);SLOT(dv,40,GetDepth);SLOT(dv,41,Begin);SLOT(dv,42,End);SLOT(dv,47,Viewport);
    SLOT(dv,57,Render);SLOT(dv,59,StateBlock);SLOT(dv,65,Bind);SLOT(dv,67,Stage);SLOT(dv,69,Sampler);SLOT(dv,83,DrawUP);SLOT(dv,89,FVF);
    SLOT(dv,92,VS);SLOT(dv,102,Frequency);SLOT(dv,107,PS);SLOT(tv,19,LockTexture);SLOT(tv,20,UnlockTexture);SLOT(bv,4,Capture);SLOT(bv,5,Apply);SLOT(av,11,Msaa);SLOT(cv,9,Params);
#undef SLOT
    back={};back.vt=sv;back.refs=1;back.desc={D3DFMT_X8R8G8B8,D3DRTYPE_SURFACE,D3DUSAGE_RENDERTARGET,D3DPOOL_DEFAULT,D3DMULTISAMPLE_4_SAMPLES,0,800,240};
    for(UINT i=0;i<4;++i){hostRT[i]=back;hostRT[i].desc.Width=640;targets[i]=&hostRT[i];}
    depth=back;depth.desc.Format=D3DFMT_D24S8;depth.desc.Usage=D3DUSAGE_DEPTHSTENCIL;currentDepth=&depth;
    present={};present.BackBufferWidth=800;present.BackBufferHeight=240;present.BackBufferFormat=D3DFMT_X8R8G8B8;present.MultiSampleType=D3DMULTISAMPLE_4_SAMPLES;present.SwapEffect=D3DSWAPEFFECT_DISCARD;present.Windowed=TRUE;
    // Deliberately non-default sentinel values in every state category.
    memset(&state,0,sizeof(state));for(size_t i=0;i<sizeof(state)/4;++i){DWORD word=static_cast<DWORD>(0x10000+i);memcpy(reinterpret_cast<BYTE*>(&state)+4*i,&word,4);}
    state.viewport={13,17,640,200,.2f,.8f};state.scissor={19,23,401,133};
}
static void EqualState(const State& before,const char* mode,bool verbose) {
    // Check and label each scalar independently, including state not mutated.
    const struct {const char* name;size_t first,last;} ranges[]={
#define RANGE(field) {#field,offsetof(State,field)/4,(offsetof(State,field)+sizeof(before.field))/4}
        RANGE(render),RANGE(stage),RANGE(sampler),RANGE(textures),RANGE(streams),RANGE(decl),RANGE(vs),RANGE(ps),RANGE(indices),RANGE(fvf),RANGE(viewport),RANGE(scissor),RANGE(transforms),RANGE(clip),RANGE(vsConstants),RANGE(psConstants),RANGE(material),RANGE(lightEnabled),RANGE(lights),RANGE(vsInt),RANGE(psInt),RANGE(vsBool),RANGE(psBool),RANGE(vertexTextures),RANGE(palette)
#undef RANGE
    };
    for(const auto& r:ranges)for(size_t i=r.first;i<r.last;++i){DWORD a,b;memcpy(&a,reinterpret_cast<const BYTE*>(&before)+4*i,4);memcpy(&b,reinterpret_cast<const BYTE*>(&state)+4*i,4);++checks;bool equal=a==b;if(!equal)++failures;if(verbose||!equal)printf("state-compare mode=%s category=%s index=%u before=%08lx after=%08lx equal=%u\n",mode,r.name,static_cast<unsigned>(i-r.first),a,b,equal);}
    for(UINT i=0;i<4;++i){char label[80];snprintf(label,sizeof(label),"%s render-target-%u restored",mode,i);Check(targets[i]==&hostRT[i],label);}
    Check(currentDepth==&depth,"depth surface restored explicitly");
    Check(!scene && !source.locked && !source.held,"owned scene/locks/DC closed");
    Check(back.refs==1 && source.refs==0 && texture.refs==0 && block.refs==0 && api.refs==1 && chain.refs==1,"all transient COM references released");
    bool refs=depth.refs==1;for(auto& rt:hostRT)refs=refs&&rt.refs==1;Check(refs,"saved target references released");
}
static void Log(const char* text){puts(text);}
int wmain() {
    setvbuf(stdout,nullptr,_IONBF,0);Initialize();auto d=reinterpret_cast<IDirect3DDevice9*>(&device);
    puts("EVIDENCE synthetic-COM-MSAA-fixture; real GDI bitmap; not GPU/game pixels; same renderer as OVERLAY_PRODUCT");
    IDirect3DSurface9* oldSource=nullptr;d->CreateOffscreenPlainSurface(580,32,D3DFMT_X8R8G8B8,D3DPOOL_SYSTEMMEM,&oldSource,nullptr);
    POINT point={10,10};HRESULT old=d->UpdateSurface(oldSource,nullptr,reinterpret_cast<IDirect3DSurface9*>(&back),&point);
    printf("osd-draw-step name=UpdateSurface hr=%08lx fixture=legacy-api\n",static_cast<unsigned long>(old));Check(old==D3DERR_INVALIDCALL,"FIRST reproduce old copy rejection due only to target MSAA");oldSource->Release();
    const State before=state;Trace trace{Log,true};calls=0;names.clear();HRESULT result=Draw(d,L"RA3 状态：已连接 | 新纹理夹具",trace);
    Check(result==S_OK && draws==1,"THEN same MSAA fixture new production draw succeeds");Check(updates==1,"production path never calls UpdateSurface");EqualState(before,"success",true);
    const auto successNames=names;
    // Fail each API before/within the draw transaction. Restoration must still run.
    size_t lastDraw=0;for(size_t i=0;i<successNames.size();++i)if(successNames[i]=="DrawPrimitiveUP")lastDraw=i;
    for(size_t i=0;i<=lastDraw;++i){calls=0;failAt=static_cast<int>(i+1);names.clear();Trace quiet{nullptr,false};HRESULT hr=Draw(d,L"failure fixture",quiet);char label[192];snprintf(label,sizeof(label),"injected call=%u name=%s returns failure",static_cast<unsigned>(i+1),successNames[i].c_str());Check(FAILED(hr),label);EqualState(before,"injected",false);}
    failAt=-1;calls=0;Trace noLog{nullptr,false};Check(Draw(d,L"no log sink",noLog)==S_OK,"logging unavailable does not change renderer outcome");EqualState(before,"no-log",false);
    for(size_t i=lastDraw+1;i<successNames.size();++i) {
        calls=0;failAt=static_cast<int>(i+1);names.clear();Trace quiet{nullptr,false};
        Check(FAILED(Draw(d,L"restore failure",quiet)),"restoration/EndScene failure is returned, never reported as success");
        Check(names.back()=="ApplyStateBlock","all restoration steps attempted even after cleanup error");
        if(successNames[i]=="ApplyStateBlock") Check(memcmp(&before,&state,sizeof(state))!=0,"negative control: failed Apply leaves differences detectable by snapshots");
        // A rejected restoration cannot promise equality. Reset ONLY fixture state
        // here so the next independent failure case starts with the same snapshot.
        state=before;for(UINT j=0;j<4;++j) targets[j]=&hostRT[j];currentDepth=&depth;scene=false;
    }
    failAt=-1;
    for(const auto format:{D3DFMT_X8R8G8B8,D3DFMT_A8R8G8B8,D3DFMT_R5G6B5}) for(const auto ms:{D3DMULTISAMPLE_NONE,D3DMULTISAMPLE_2_SAMPLES,D3DMULTISAMPLE_4_SAMPLES,D3DMULTISAMPLE_8_SAMPLES}) {
        back.desc.Format=format;back.desc.MultiSampleType=ms;present.BackBufferFormat=format;present.MultiSampleType=ms;
        Trace variant{nullptr,false};HRESULT hr=Draw(d,L"format and MSAA variant",variant);
        printf("fixture-variant Format=%u MultiSampleType=%u textureFormat=21 hr=%08lx\n",format,ms,static_cast<unsigned long>(hr));
        Check(hr==S_OK,"texture draw does not require source/target format match or MSAA NONE");EqualState(before,"variant",false);
    }
    back.desc.Format=D3DFMT_X8R8G8B8;back.desc.MultiSampleType=D3DMULTISAMPLE_4_SAMPLES;
    present.BackBufferFormat=back.desc.Format;present.MultiSampleType=back.desc.MultiSampleType;
    Check(device.vt==dv && device.refs==1,"device object/vptr unchanged");
    printf("RESULT texture-draw checks=%d failures=%d skipped=0 fixture=synthetic\n",checks,failures);return failures?1:0;
}
