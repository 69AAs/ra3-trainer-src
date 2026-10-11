// Compile the actual harness installation path and its actual hostTable storage.
// No GPU or driver method is called; executable synthetic prologue is relocated.
#define wmain HarnessMain
#include "harness.cpp"
#undef wmain

int wmain() {
    setvbuf(stdout,NULL,_IONBF,0);
    int tokens[119]={};
    void* source[119];
    for (size_t i=0;i<119;i++) source[i]=&tokens[i];
    BYTE* code=static_cast<BYTE*>(VirtualAlloc(nullptr,64,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    if (!code) return 2;
    const BYTE body[]={0xb8,0x34,0x12,0,0,0xc2,20,0};
    memset(code,0x90,64); memcpy(code,body,sizeof(body)); DWORD old;
    if (!VirtualProtect(code,64,PAGE_EXECUTE_READ,&old)) return 2;
    source[17]=code;
    struct FakeDevice { void** table; BYTE privateState[64]; } fake={source,{}};
    FakeDevice before=fake;
    auto target=reinterpret_cast<IDirect3DDevice9*>(&fake);
    Check(sizeof(hostTable)/sizeof(hostTable[0])==119,"actual harness copy length equals ABI 119");
    Check(sizeof(hostTable)==476,"actual x86 copy is 476 bytes");
    Check(InstallCapture(target),"actual harness install accepts complete source");
    Check(!memcmp(&fake,&before,sizeof(fake)),"entire device object unchanged after install");
    Check(realTable==source && realPresent(target,nullptr,nullptr,nullptr,nullptr)==0x1234,"original Present semantics retained by executable trampoline");
    Check(hostTable[17]==source[17] && captureHook.Equals(captureHook.patch),"real Present slot unchanged; function code patched");
    bool nonnull=true, equal=true;
    for (size_t i=0;i<119;i++) {
        nonnull=nonnull && hostTable[i]!=nullptr;
        if (i!=17) equal=equal && hostTable[i]==source[i];
    }
    Check(nonnull,"all 119 patched slots nonnull");
    Check(equal,"every non-Present slot equals corresponding source pointer");
    Check(hostTable[43]==source[43] && hostTable[118]==source[118],"Clear and last slot preserved");
    if (!captureHook.Restore() || memcmp(code,body,sizeof(body))) return 2;
    captureHook.Release(); // Quiescent teardown; exact original code verified.
    bool rejected=true, unchanged=true;
    for (size_t i=0;i<119;i++) {
        void* saved=source[i]; source[i]=nullptr;
        rejected=!InstallCapture(target) && rejected;
        unchanged=fake.table==source && unchanged;
        source[i]=saved;
    }
    Check(rejected,"NULL at each of 119 source slots rejects install, including Present");
    Check(unchanged,"all rejected installs leave device vptr unchanged");
    void* shortCopy[18]={};
    Check(!PrepareDeviceTable(source,shortCopy,reinterpret_cast<void*>(&CapturePresent)),"18-slot prefix rejected before memcpy");
    Check(shortCopy[0]==nullptr && shortCopy[17]==nullptr,"short destination not written");
    void* candidate[119]={};
    Check(PrepareDeviceTable(source,candidate,reinterpret_cast<void*>(&CapturePresent)),"complete candidate prepared");
    candidate[43]=nullptr;
    Check(!ValidateDeviceTable(source,candidate,reinterpret_cast<void*>(&CapturePresent)),"post-patch NULL Clear rejected");
    candidate[43]=source[42];
    Check(!ValidateDeviceTable(source,candidate,reinterpret_cast<void*>(&CapturePresent)),"nonnull but wrong non-Present pointer rejected");
    candidate[43]=source[43]; candidate[17]=source[17];
    Check(!ValidateDeviceTable(source,candidate,reinterpret_cast<void*>(&CapturePresent)),"unpatched Present rejected");
    Check(!PrepareDeviceTable(source,candidate,nullptr),"null replacement rejected");
    Check(!InstallCapture(nullptr),"null device rejected");
    fake.table=nullptr;
    Check(!InstallCapture(target) && fake.table==nullptr,"null source table rejected without publication");
    printf("RESULT harness-table checks=%d failures=%d (actual install and trampoline; no GPU calls)\n",checks,failures);
    VirtualFree(code,0,MEM_RELEASE);
    return failures?1:0;
}
