// Reuse the unchanged synthetic COM fixture, replace only its texture/quad endpoints.
// This compiles the same two feature defines as the shipped product DLL.
#define device fixtureDevice
#define wmain HistoricalTextureFixtureMain
#include "texture-draw-tests.cpp"
#undef wmain
#undef device
#define OVERLAY_PRODUCT
#define OVERLAY_PANEL
#include "overlay.cpp"
bool ProductEntryOwned(void*) {return false;}
void ProductDescribeEntry(const char*,void*) {}
static DWORD panelPixels[512][528];
static int locks,forwarded;
static HRESULT hostResult=S_OK;
static UINT expectX=24,expectY=24;
static DWORD expectedChecksum;
static HRESULT WINAPI PanelCaps(Object*,D3DCAPS9* out) {CALL("GetDeviceCaps");*out={};out->NumSimultaneousRTs=4;out->MaxStreams=16;out->MaxTextureWidth=out->MaxTextureHeight=4096;return S_OK;}
static HRESULT WINAPI PanelTexture(Object*,UINT w,UINT h,UINT levels,DWORD usage,D3DFORMAT fmt,D3DPOOL pool,IDirect3DTexture9** out,HANDLE*) {
    CALL("CreateTexture");if(w!=512||h!=512||levels!=1||usage||fmt!=D3DFMT_A8R8G8B8||pool!=D3DPOOL_MANAGED)return D3DERR_NOTAVAILABLE;
    ++texture.refs;*out=reinterpret_cast<IDirect3DTexture9*>(&texture);return S_OK;
}
static HRESULT WINAPI PanelLock(Object*,UINT,D3DLOCKED_RECT* out,const RECT*,DWORD) {CALL("LockTexture");++locks;*out={sizeof(panelPixels[0]),panelPixels};return S_OK;}
static HRESULT WINAPI PanelQuad(Object*,D3DPRIMITIVETYPE type,UINT count,const void* vertices,UINT stride) {
    CALL("DrawPrimitiveUP");++draws;
    Check(scene && targets[0]==&back && !currentDepth && !targets[1] && !targets[2] && !targets[3],"panel draws to actual MSAA target inside owned scene");
    Check(type==D3DPT_TRIANGLELIST && count==2 && stride==24,"panel draws exactly two textured triangles");
    const float* v=static_cast<const float*>(vertices);
    Check(v[0]==expectX-.5f && v[1]==expectY-.5f && v[6]==expectX+359.5f && v[13]==expectY+439.5f && v[10]==360.f/512 && v[17]==440.f/512,"panel mapping position/size and POT UV coordinates");
    Check(state.render[D3DRS_ALPHABLENDENABLE] && state.render[D3DRS_SRCBLEND]==D3DBLEND_ONE && state.render[D3DRS_DESTBLEND]==D3DBLEND_INVSRCALPHA,"Pbgra32 premultiplied-alpha blend contract");
    DWORD checksum=2166136261u;
    for(UINT y=0;y<440;++y)for(UINT x=0;x<360*4;++x)checksum=(checksum^reinterpret_cast<BYTE*>(panelPixels[y])[x])*16777619u;
    Check(checksum==expectedChecksum,"every WPF/synthetic source pixel survives pitch-aware upload unchanged");
    Check(panelPixels[0][400]==0 && panelPixels[500][0]==0,"POT padding initialized transparent");
    state.streams[0]={nullptr,0,0,state.streams[0].freq};return S_OK;
}
static HRESULT WINAPI HostPresent(IDirect3DDevice9*,const RECT*,const RECT*,HWND,const RGNDATA*) {++forwarded;return hostResult;}
static HRESULT WINAPI HostReset(IDirect3DDevice9*,D3DPRESENT_PARAMETERS*) {return S_OK;}
static DWORD WINAPI Invoke(void*) {return static_cast<DWORD>(Present(device,nullptr,nullptr,nullptr,nullptr));}
static void Run(bool different=false) {
    if(!different) {Check(Invoke(nullptr)==static_cast<DWORD>(hostResult),"original Present result unchanged");return;}
    HANDLE thread=CreateThread(nullptr,0,Invoke,nullptr,0,nullptr);Check(thread!=nullptr,"real worker created");
    if(!thread)return;
    const DWORD waitResult=WaitForSingleObject(thread,10000);
    Check(waitResult==WAIT_OBJECT_0,"worker callback completes");
    // Bail out on timeout: the worker may still be mutating state/stats/panelPixels.
    if(waitResult!=WAIT_OBJECT_0){CloseHandle(thread);return;}
    CloseHandle(thread);
}
static void StateEqual(const State& before) {
    Check(!memcmp(&before,&state,sizeof(state)),"all render/texture/stream/shader/constant/viewport states restored byte-for-byte");
    for(UINT i=0;i<4;++i)Check(targets[i]==&hostRT[i] && hostRT[i].refs==1,"RT binding and refs restored");
    Check(currentDepth==&depth && depth.refs==1 && !scene && block.refs==0 && back.refs==1,"depth, scene and transient refs restored");
}
int wmain(int argc,wchar_t** argv) {
    setvbuf(stdout,nullptr,_IONBF,0);
    puts("EVIDENCE M1 WPF bitmap -> synthetic texture -> quad; actual product Present; not game/GPU pixels");
    Initialize();
    dv[7]=reinterpret_cast<void*>(&PanelCaps);dv[23]=reinterpret_cast<void*>(&PanelTexture);dv[83]=reinterpret_cast<void*>(&PanelQuad);tv[19]=reinterpret_cast<void*>(&PanelLock);
    back.desc.Width=1920;back.desc.Height=1080;present.BackBufferWidth=1920;present.BackBufferHeight=1080;
    std::vector<BYTE> bytes(PanelOsd::MappingSize);
    auto& h=*reinterpret_cast<PanelOsd::Header*>(bytes.data());
    if(argc>1) {
        FILE* file=_wfopen(argv[1],L"rb");if(!file){puts("FAIL WPF frame missing");return 1;}
        const size_t count=fread(bytes.data(),1,bytes.size(),file);fclose(file);
        Check(count==bytes.size(),"full cross-language C# mapping artifact read");
    } else {
        h.magic=PanelOsd::Magic;h.size=PanelOsd::MappingSize;h.abi=1;h.enabled=1;h.width=360;h.height=440;h.stride=1440;
        h.format=1;h.generation=1;h.x=h.y=24;h.pixelOffset=128;h.eventOffset=128+PanelOsd::PixelCapacity;h.eventBytes=4096;h.pixelBytes=360*440*4;h.epoch=1;
        for(DWORD i=0;i<h.pixelBytes;++i)bytes[128+i]=static_cast<BYTE>(i*7);
        h.checksum=PanelOsd::Checksum(bytes.data()+128,h.pixelBytes);
    }
    h.targetPid=GetCurrentProcessId();h.hostPid=GetCurrentProcessId();h.heartbeat=GetTickCount();h.sequence=2;
    expectedChecksum=h.checksum;expectX=h.x;expectY=h.y;
    Check(PanelOsd::Validate(h,GetCurrentProcessId(),GetTickCount())==nullptr,"C# / C++ ABI and bounds agree");
    h.reserved[0]=1;Check(PanelOsd::Validate(h,GetCurrentProcessId(),GetTickCount())==nullptr && PanelOsd::PanelMode(h),"panel mode 1 is a valid header (M2a)");
    h.reserved[0]=2;Check(!strcmp(PanelOsd::Validate(h,GetCurrentProcessId(),GetTickCount()),"mode-invalid"),"panel mode above 1 fails closed");
    h.reserved[0]=1;h.reserved[1]=1;Check(!strcmp(PanelOsd::Validate(h,GetCurrentProcessId(),GetTickCount()),"reserved-nonzero"),"M2/M3 reserved counters must stay zero");
    h.reserved[1]=0;h.reserved[0]=0;
    PanelOsd::Reader missing;Check(missing.Read()==S_FALSE && !strcmp(missing.reason,"mapping-missing"),"missing mapping fails closed, no file created");
    FILETIME c,e,k,u;GetProcessTimes(GetCurrentProcess(),&c,&e,&k,&u);ULONGLONG identity=(static_cast<ULONGLONG>(c.dwHighDateTime)<<32)|c.dwLowDateTime;
    WCHAR name[96];swprintf(name,96,L"Local\\Ra3Trainer.Panel.v1.%lu.%016llX",GetCurrentProcessId(),identity);
    HANDLE mapping=CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,PanelOsd::MappingSize,name);
    BYTE* view=static_cast<BYTE*>(MapViewOfFile(mapping,FILE_MAP_WRITE,0,0,PanelOsd::MappingSize));
    Check(mapping && view,"pagefile mapping created");if(!view)return 1;
    memcpy(view,bytes.data(),bytes.size());auto shared=reinterpret_cast<PanelOsd::Header*>(view);
    device=reinterpret_cast<IDirect3DDevice9*>(&fixtureDevice);owner=GetCurrentThreadId();nextPresent=HostPresent;nextReset=HostReset;
    ProductStatus status={};status.magic=0x3544534f;status.size=356;status.targetPid=GetCurrentProcessId();status.enabled=status.connected=1;productView=&status;
    auto fresh=[&](){status.heartbeat=shared->heartbeat=GetTickCount();stats={1,0,0,0,S_OK};calls=0;failAt=-1;forwarded=0;draws=0;drawDiagnostics=true;};
    fresh();const State before=state;Run();Check(draws==1 && locks==1 && forwarded==1,"first published panel draws through product callback");StateEqual(before);
    const auto firstNames=names;
    const ULONGLONG uploaded=panelCache.uploadedBytes;
    for(int i=0;i<20;++i)Run();
    Check(locks==1 && panelCache.uploadedBytes==uploaded && draws==21,"stationary content: 20 frames upload zero bytes");StateEqual(before);
    shared->heartbeat=GetTickCount();Run();Check(locks==1,"heartbeat does not dirty texture");
    shared->x=42;++shared->generation;shared->sequence+=2;expectX=42;Run();Check(locks==2,"position change published and uploaded once");
    shared->x=8192;shared->y=8192;++shared->generation;shared->sequence+=2;expectX=1560;expectY=640;Run();Check(draws==24,"oversized requested position clamped to target");
    shared->x=shared->y=24;++shared->generation;shared->sequence+=2;expectX=expectY=24;
    for(auto format:{D3DFMT_X8R8G8B8,D3DFMT_A8R8G8B8,D3DFMT_R5G6B5})for(auto ms:{D3DMULTISAMPLE_NONE,D3DMULTISAMPLE_2_SAMPLES,D3DMULTISAMPLE_4_SAMPLES,D3DMULTISAMPLE_8_SAMPLES}) {
        fresh();back.desc.Format=format;back.desc.MultiSampleType=ms;present.BackBufferFormat=format;present.MultiSampleType=ms;Run();Check(draws==1 && stats.error==S_OK,"supported target format / MSAA matrix draws");StateEqual(before);
    }
    fresh();back.desc.Format=D3DFMT_D24S8;Run();Check(!draws && FAILED(stats.error) && forwarded==1,"unsupported target format fails closed and forwards host");back.desc.Format=D3DFMT_X8R8G8B8;
    // Fail each actual pipeline API, including upload, MSAA query and state restoration.
    for(size_t i=0;i<firstNames.size();++i) {
        panelCache.Release();fresh();calls=0;failAt=static_cast<int>(i+1);names.clear();Run();
        Check(FAILED(stats.error) && !stats.enabled && forwarded==1,"each injected panel API failure stops drawing without blocking Present");
        const bool restore=firstNames[i]=="ApplyStateBlock" || firstNames[i]=="SetRenderTarget" || firstNames[i]=="SetDepthStencilSurface";
        if(!restore)StateEqual(before);
        if(firstNames[i]=="ApplyStateBlock")Check(memcmp(&before,&state,sizeof(state))!=0,"negative control detects failed Apply");
        state=before;for(UINT j=0;j<4;++j)targets[j]=&hostRT[j];currentDepth=&depth;scene=false;
    }
    for(bool mt:{false,true})for(bool different:{false,true}) {
        fresh();creationFlags=D3DCREATE_SOFTWARE_VERTEXPROCESSING|(mt?D3DCREATE_MULTITHREADED:0);Run(different);
        bool allowed=!different||mt;
        Check(forwarded==1 && (allowed?(draws==1 && stats.error==S_OK):(!draws && FAILED(stats.error))),"shipped panel callback preserves r35 four-way thread admission");StateEqual(before);
        if(!allowed){Run(different);Check(!stats.enabled && !draws,"thread failure cannot revive");}
    }
    creationFlags=0;fresh();Run();status.enabled=0;Run();Check(!stats.enabled,"status stop disables panel");status.enabled=1;panelReader.retry=0;Run();Check(stats.enabled,"explicit reenable reopens panel mapping");
    fresh();Run();int previous=locks;Check(Reset(device,&present)==S_OK,"host Reset unchanged");Run();Check(locks==previous+1,"Reset invalidates upload generation");
    fresh();failNext=1;Run();Check(FAILED(stats.error),"injected renderer failure disables panel");Run();Check(!stats.enabled,"failed panel never revives");Check(Reset(device,&present)==S_OK && !stats.enabled,"Reset cannot revive fault");
    fresh();hostResult=D3DERR_DEVICELOST;Run();Check(stats.error==D3DERR_DEVICELOST && !stats.enabled,"host device-lost forwarded and stops panel");hostResult=S_OK;
    fresh();Check(!OverlayStop(FALSE) && !stats.enabled && device,"full撤 stop retains hooks/device without waiting IPC");
    // Malformed snapshots are rejected before COM. Restore each field from a valid snapshot.
    const auto valid=*shared;
    for(int kind=0;kind<8;++kind) {
        *shared=valid;shared->heartbeat=GetTickCount();shared->generation+=100+kind;shared->sequence+=2;
        switch(kind){case 0:++shared->abi;break;case 1:shared->width=513;break;case 2:shared->format=9;break;case 3:++shared->checksum;break;case 4:shared->pixelBytes=0xffffffff;break;case 5:shared->eventOffset=0;break;case 6:shared->targetPid=0;break;case 7:shared->reserved[1]=1;break;}
        fresh();Run();Check(FAILED(stats.error) && calls==0 && !draws && forwarded==1,"invalid ABI/bounds/format/checksum/identity/reserved fail closed before COM");
    }
    *shared=valid;shared->heartbeat=GetTickCount();shared->sequence|=1;fresh();Run();Check(!draws && calls==0,"writer busy produces no draw");
    *shared=valid;shared->heartbeat=GetTickCount()-6000;stats={1,0,0,0,S_OK};calls=0;Run();Check(!calls,"expired pixel heartbeat fails closed");
    panelReader.Close();panelCache.Release();productView=nullptr;device=nullptr;UnmapViewOfFile(view);CloseHandle(mapping);
    HANDLE gone=OpenFileMappingW(FILE_MAP_READ,FALSE,name);Check(!gone,"all panel mapping handles close: named object gone, no disk residue");if(gone)CloseHandle(gone);
          // ---- M2c: self-drawn cursor ----
          {
              UINT cx=0,cy=0;
              PanelOsd::CursorRect(100,200,1920,1080,cx,cy);
              Check(cx==100 && cy==200,"M2c cursor maps 1:1 at a plain position (same space as the back buffer)");
              PanelOsd::CursorRect(-5,-9,1920,1080,cx,cy);
              Check(cx==0 && cy==0,"M2c cursor clamps negative coordinates to the surface origin");
              PanelOsd::CursorRect(1919,1079,1920,1080,cx,cy);
              Check(cx==1920-PanelOsd::CursorSize && cy==1080-PanelOsd::CursorSize,"M2c cursor clamps at the far edge so it can never leave the target");
              PanelOsd::CursorRect(0,0,16,16,cx,cy);
              Check(cx==0 && cy==0,"M2c cursor survives a target smaller than the sprite");
              Check(sizeof(PanelOsd::kCursorSprite)==PanelOsd::CursorSize*PanelOsd::CursorSize*4,"M2c cursor sprite is a 24x24 BGRA block");
              int opaque=0,transparent=0;
              for(UINT i=0;i<PanelOsd::CursorSize*PanelOsd::CursorSize;++i) {
                  const BYTE a=PanelOsd::kCursorSprite[i*4+3];
                  if(a>200) ++opaque; else if(a==0) ++transparent;
              }
              Check(opaque>60 && transparent>300,"M2c cursor sprite has a solid body and real transparency around it");
              Check(PanelOsd::kCursorSprite[3]==0,"M2c cursor corner pixel is transparent so the hot spot is the arrow tip");
          }
    printf("RESULT panel checks=%d failures=%d skipped=0\n",checks,failures);return failures?1:0;
}
