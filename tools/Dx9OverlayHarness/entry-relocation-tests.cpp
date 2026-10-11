// All byte fixtures are synthetic PE files, not claims about host pixels.
#include "product-proxy.cpp"
#include <initializer_list>
extern "C" BOOL __cdecl OverlayStart(IDirect3DDevice9*) noexcept { return FALSE; }
static int checks=0,failures=0;
static void Check(bool ok,const char* name) { ++checks; if(!ok) ++failures; printf("%s %s\n",ok?"PASS":"FAIL",name); }
struct Fixture {
    BYTE file[0x800]={}; BYTE live[32]={}; DWORD rva=0x1020;
    Fixture(const char* hex,unsigned operand=0xffff) {
        auto dos=reinterpret_cast<IMAGE_DOS_HEADER*>(file); dos->e_magic=IMAGE_DOS_SIGNATURE; dos->e_lfanew=0x80;
        auto nt=reinterpret_cast<IMAGE_NT_HEADERS32*>(file+0x80); nt->Signature=IMAGE_NT_SIGNATURE;
        nt->FileHeader.Machine=IMAGE_FILE_MACHINE_I386; nt->FileHeader.NumberOfSections=2;
        nt->FileHeader.SizeOfOptionalHeader=sizeof(IMAGE_OPTIONAL_HEADER32);
        nt->OptionalHeader.Magic=IMAGE_NT_OPTIONAL_HDR32_MAGIC; nt->OptionalHeader.ImageBase=0x10000000;
        nt->OptionalHeader.SizeOfImage=0x3000; nt->OptionalHeader.SizeOfHeaders=0x200;
        nt->OptionalHeader.NumberOfRvaAndSizes=16;
        nt->OptionalHeader.DataDirectory[5]={0x2000,12};
        auto sections=reinterpret_cast<IMAGE_SECTION_HEADER*>(nt+1);
        sections[0].VirtualAddress=0x1000; sections[0].Misc.VirtualSize=0x400;
        sections[0].SizeOfRawData=0x400; sections[0].PointerToRawData=0x200; sections[0].Characteristics=IMAGE_SCN_MEM_EXECUTE;
        sections[1].VirtualAddress=0x2000; sections[1].Misc.VirtualSize=0x200;
        sections[1].SizeOfRawData=0x200; sections[1].PointerToRawData=0x600;
        auto reloc=reinterpret_cast<IMAGE_BASE_RELOCATION*>(file+0x600); reloc->VirtualAddress=0x1000; reloc->SizeOfBlock=12;
        WORD item=operand==0xffff?0:static_cast<WORD>(0x3000+0x20+operand); memcpy(file+0x608,&item,2);
        for(unsigned i=0;i<32;++i) { unsigned value=0; sscanf(hex+2*i,"%2x",&value); live[i]=static_cast<BYTE>(value); }
        memcpy(file+0x220,live,32);
    }
    EntryVerification Run(const char* name,const char* expected,DWORD base=0x10000000,bool identity=true) {
        EntryImage image; bool parsed=image.Parse(file,sizeof(file));
        auto result=parsed?VerifyImageEntry(image,rva,base,live,identity):EntryVerification{};
        char raw[65],disk[65]="unavailable",normalized[65]="unavailable"; EntryHex(live,32,raw);
        if(result.diskRead) EntryHex(result.disk,32,disk);
        if(result.normalized) EntryHex(result.expected,32,normalized);
        char table[25]; EntryHex(file+0x600,12,table);
        printf("FIXTURE %s raw=%s disk=%s expected=%s delta=0x%08lX relocations=%u relocTable=%s identity=%u diskRead=%u reason=%s install=%s\n",
               name,raw,disk,normalized,result.delta,result.relocations,table,identity?1u:0u,result.diskRead?1u:0u,result.reason,strcmp(result.reason,"verified-system")?"refused":"allowed");
        Check(!strcmp(result.reason,expected),name); return result;
    }
    void Relocate(unsigned operand,DWORD delta) { DWORD v; memcpy(&v,live+operand,4); v+=delta; memcpy(live+operand,&v,4); }
};
int wmain() {
    const char* reset="8BFF558BEC83E4F881EC44010000A180C3151033C489842440010000538B5D08";
    const char* end="6A14B889DE0810E895E301008B75088BDEF7DB1BDB33C98D4604894DE423D889";
    const char* present="8BFF558BEC83E4F85151568B75088BCEF7D9571BC98D460423C86A00518D4C24";
    Fixture p(present); p.Run("Present-unchanged","verified-system",0x688b0000);
    Fixture a(reset,15); a.Run("Reset-delta0","verified-system"); a.Relocate(15,0x588b0000); a.Run("Reset-host-delta","verified-system",0x688b0000);
    Fixture b(end,3); b.Run("EndScene-delta0","verified-system"); b.Relocate(3,0x588b0000); b.Run("EndScene-host-delta","verified-system",0x688b0000);
    a.live[10]^=1; a.Run("nonrelocation-edit","patched-nonrelocation-byte",0x688b0000); a.live[10]^=1;
    a.live[15]^=1; a.Run("relocation-wrong-value","image-mismatch",0x688b0000); a.live[15]^=1;
    a.Run("identity-mismatch","image-mismatch",0x688b0000,false);
    Fixture wrap(reset,15); wrap.Relocate(15,0xf0000000); wrap.Run("delta-negative-modulo32","verified-system",0);
    Fixture overflow(reset,15); DWORD high=0xfffffff0; memcpy(overflow.file+0x220+15,&high,4); memcpy(overflow.live+15,&high,4);
    overflow.Relocate(15,0x20); overflow.Run("addition-overflow","verified-system",0x10000020);
    Fixture missing(reset); missing.Relocate(15,0x588b0000); missing.Run("same-delta-without-reloc-record","patched-nonrelocation-byte",0x688b0000);
    Fixture malformed(reset,15); malformed.file[0x604]=7; malformed.Run("short-block","unidentified");
    Fixture unsupported(reset,15); unsupported.file[0x609]=0xa0; unsupported.Run("unsupported-type","unidentified");
    Fixture duplicate(reset,15); memcpy(duplicate.file+0x60a,duplicate.file+0x608,2); duplicate.Run("duplicate-fixup","unidentified");
    Fixture truncated(reset,15); reinterpret_cast<IMAGE_NT_HEADERS32*>(truncated.file+0x80)->OptionalHeader.DataDirectory[5].Size=10; truncated.Run("truncated-directory","unidentified");
    Fixture badpe(reset,15); badpe.file[0]=0; badpe.Run("invalid-pe","unidentified");
    Fixture identityFixture(reset,15); EntryImage identityImage;
    Check(identityImage.Parse(identityFixture.file,sizeof(identityFixture.file)),"identity PE parsed");
    BYTE headers[0x200]; memcpy(headers,identityFixture.file,sizeof(headers));
    Check(identityImage.MatchesHeaders(headers,0x688b0000),"preferred header base accepted");
    auto loadedNt=reinterpret_cast<IMAGE_NT_HEADERS32*>(headers+0x80);
    loadedNt->OptionalHeader.ImageBase=0x688b0000;
    Check(identityImage.MatchesHeaders(headers,0x688b0000),"exact actual header base accepted");
    loadedNt->OptionalHeader.ImageBase=0x688b0001;
    Check(!identityImage.MatchesHeaders(headers,0x688b0000),"wrong header base rejected");
    memcpy(headers,identityFixture.file,sizeof(headers)); loadedNt->FileHeader.TimeDateStamp^=1;
    Check(!identityImage.MatchesHeaders(headers,0x688b0000),"replaced image timestamp rejected");
    memcpy(headers,identityFixture.file,sizeof(headers)); loadedNt->OptionalHeader.CheckSum^=1;
    Check(!identityImage.MatchesHeaders(headers,0x688b0000),"replaced image checksum rejected");
    for(unsigned i=0;i<32;++i) {
        Fixture edited(reset,15); edited.Relocate(15,0x588b0000); edited.live[i]^=1;
        char name[40]; snprintf(name,sizeof(name),"edit-each-byte-%u",i);
        edited.Run(name,i>=15 && i<19?"image-mismatch":"patched-nonrelocation-byte",0x688b0000);
    }
    // Relocations straddling either edge: read/adjust the full DWORD, compare its intersection.
    for(int offset: {-2,30}) {
        Fixture boundary(present); WORD item=static_cast<WORD>(0x3020+offset); memcpy(boundary.file+0x608,&item,2);
        DWORD v=0x10fffffe; memcpy(boundary.file+0x220+offset,&v,4); memcpy(boundary.live,boundary.file+0x220,32);
        v+=3; BYTE value[4]; memcpy(value,&v,4);
        for(int i=0;i<4;++i) if(offset+i>=0 && offset+i<32) boundary.live[offset+i]=value[i];
        boundary.Run(offset<0?"left-boundary-carry":"right-boundary-carry","verified-system",0x10000003);
    }
    for(bool push: {false,true}) {
        Fixture branch(present); DWORD target=static_cast<DWORD>(reinterpret_cast<uintptr_t>(&EntryModuleName));
        branch.live[0]=push?0x68:0xe9; DWORD operand=push?target:target-(0x10000000+branch.rva+5);
        memcpy(branch.live+1,&operand,4); if(push) branch.live[5]=0xc3;
        branch.Run(push?"push-ret":"jmp-rel32","entry-branch");
        auto transfer=DecodeEntryTransfer(branch.live,32,0x10000000+branch.rva); char module[MAX_PATH*3];
        EntryModuleName(reinterpret_cast<void*>(transfer.target),module,sizeof(module));
        printf("FIXTURE branch transfer=%s target=0x%08lX targetModule=%s\n",transfer.kind,static_cast<DWORD>(transfer.target),module);
        Check(transfer.resolved && transfer.target==target && !strcmp(module,"EntryRelocationTests.exe"),"branch target module resolved");
    }
    // Real Windows system DLL check, independent of a GPU or game process.
    auto fn=reinterpret_cast<void*>(Real("D3DPERF_GetStatus")); char line[4096];
    puts("FIXTURE real-system-export=D3DPERF_GetStatus; not a device Present entry or game evidence");
    ProductFormatEntry("FixtureExport",fn,line,sizeof(line)); puts(line);
    WCHAR path[MAX_PATH]={}; const DWORD pathLength=systemD3D?GetModuleFileNameW(systemD3D,path,MAX_PATH):0;
    HANDLE f=(pathLength && pathLength<MAX_PATH)?CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,0,nullptr):INVALID_HANDLE_VALUE;
    if (f==INVALID_HANDLE_VALUE) {
        printf("FIXTURE system image unreadable (pathLength=%lu): header comparison skipped\n",static_cast<unsigned long>(pathLength));
        ++failures;
    } else {
        BYTE diskHeader[4096]={},loadedHeader[4096]={}; DWORD got=0;
        if (!ReadFile(f,diskHeader,4096,&got,nullptr) || got!=4096) {
            printf("FIXTURE system image short read got=%lu\n",static_cast<unsigned long>(got));
            ++failures;
        }
        CloseHandle(f);
        ReadEntryBytes(systemD3D,loadedHeader,4096);
        for(unsigned i=0;i<1024;++i) if(diskHeader[i]!=loadedHeader[i]) printf("HEADER-DIFF offset=%u disk=%02X live=%02X\n",i,diskHeader[i],loadedHeader[i]);
    }
    Check(fn && ProductEntryOwned(fn),"real system D3D9 export verifies after relocation");
    printf("EntryRelocationTests: %d checks, %d failures, 0 skipped\n",checks,failures); return failures?1:0;
}
