#pragma once
#include "entry-diagnostics.h"
#include <stddef.h>
// PE32 file view. All offsets are checked before dereference; no mapped reloc
// directory is trusted (the loader may discard it).
struct EntryImage {
    const BYTE* file=nullptr; size_t size=0;
    IMAGE_NT_HEADERS32 nt={}; IMAGE_SECTION_HEADER sections[96]={}; DWORD pe=0;
    bool Parse(const BYTE* bytes,size_t length) {
        file=bytes; size=length; IMAGE_DOS_HEADER dos={};
        if(length<sizeof(dos)) return false;
        memcpy(&dos,bytes,sizeof(dos));
        if(dos.e_magic!=IMAGE_DOS_SIGNATURE || dos.e_lfanew<0) return false;
        pe=static_cast<DWORD>(dos.e_lfanew);
        if(pe>length || length-pe<sizeof(nt)) return false;
        memcpy(&nt,bytes+pe,sizeof(nt));
        if(nt.Signature!=IMAGE_NT_SIGNATURE || nt.FileHeader.Machine!=IMAGE_FILE_MACHINE_I386 ||
           nt.OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC ||
           nt.FileHeader.SizeOfOptionalHeader!=sizeof(IMAGE_OPTIONAL_HEADER32) ||
           !nt.FileHeader.NumberOfSections || nt.FileHeader.NumberOfSections>96 ||
           nt.OptionalHeader.NumberOfRvaAndSizes>IMAGE_NUMBEROF_DIRECTORY_ENTRIES ||
           nt.OptionalHeader.SizeOfHeaders>length || !nt.OptionalHeader.SizeOfImage) return false;
        size_t offset=static_cast<size_t>(pe)+sizeof(nt), count=nt.FileHeader.NumberOfSections*sizeof(IMAGE_SECTION_HEADER);
        if(offset>length || count>length-offset || offset+count>nt.OptionalHeader.SizeOfHeaders) return false;
        memcpy(sections,bytes+offset,count);
        for(unsigned i=0;i<nt.FileHeader.NumberOfSections;++i) {
            const auto& s=sections[i]; DWORD span=s.Misc.VirtualSize>s.SizeOfRawData?s.Misc.VirtualSize:s.SizeOfRawData;
            if(s.VirtualAddress<nt.OptionalHeader.SizeOfHeaders || s.VirtualAddress>nt.OptionalHeader.SizeOfImage ||
               span>nt.OptionalHeader.SizeOfImage-s.VirtualAddress || s.PointerToRawData>length || s.SizeOfRawData>length-s.PointerToRawData) return false;
            if(s.SizeOfRawData && s.PointerToRawData<nt.OptionalHeader.SizeOfHeaders) return false;
            for(unsigned j=0;j<i;++j) {
                const auto& t=sections[j]; DWORD ts=t.Misc.VirtualSize>t.SizeOfRawData?t.Misc.VirtualSize:t.SizeOfRawData;
                if(static_cast<uint64_t>(s.VirtualAddress)<static_cast<uint64_t>(t.VirtualAddress)+ts &&
                   static_cast<uint64_t>(t.VirtualAddress)<static_cast<uint64_t>(s.VirtualAddress)+span) return false;
                if(s.SizeOfRawData && t.SizeOfRawData &&
                   static_cast<uint64_t>(s.PointerToRawData)<static_cast<uint64_t>(t.PointerToRawData)+t.SizeOfRawData &&
                   static_cast<uint64_t>(t.PointerToRawData)<static_cast<uint64_t>(s.PointerToRawData)+s.SizeOfRawData) return false;
            }
        }
        return true;
    }
    const BYTE* At(DWORD rva,size_t count,bool executable=false) const {
        for(unsigned i=0;i<nt.FileHeader.NumberOfSections;++i) {
            const auto& s=sections[i];
            if(rva>=s.VirtualAddress && rva-s.VirtualAddress<=s.SizeOfRawData && count<=s.SizeOfRawData-(rva-s.VirtualAddress) &&
               (!executable || (s.Characteristics&IMAGE_SCN_MEM_EXECUTE))) return file+s.PointerToRawData+(rva-s.VirtualAddress);
        }
        return nullptr;
    }
    bool MatchesHeaders(BYTE* loaded,DWORD actualBase) const {
        // Windows may publish the actual base in the mapped optional header.
        // Accept only the disk preferred value or that exact allocation base;
        // all other header bytes (including section/reloc directories) match.
        size_t offset=pe+offsetof(IMAGE_NT_HEADERS32,OptionalHeader)+offsetof(IMAGE_OPTIONAL_HEADER32,ImageBase);
        DWORD base; memcpy(&base,loaded+offset,4);
        if(base!=nt.OptionalHeader.ImageBase && base!=actualBase) return false;
        memcpy(loaded+offset,&nt.OptionalHeader.ImageBase,4);
        return memcmp(loaded,file,nt.OptionalHeader.SizeOfHeaders)==0;
    }
};
struct EntryVerification {
    const char* reason="unidentified";
    BYTE disk[32]={}, expected[32]={}, covered[32]={}; bool diskRead=false, normalized=false;
    DWORD delta=0; unsigned relocations=0;
};
// Identity is compared separately with the loaded image headers by the caller.
// A branch in an unidentified allocation stays unidentified; identified entries
// are classified as branch before comparison. Neither result permits installation.
static EntryVerification VerifyImageEntry(const EntryImage& image,DWORD rva,DWORD actualBase,
                                         const BYTE* live,bool identity) {
    EntryVerification result;
    if(!identity) { result.reason="image-mismatch"; return result; }
    const BYTE* original=image.At(rva,32,true);
    if(!original) return result;
    memcpy(result.disk,original,32); memcpy(result.expected,original,32); result.diskRead=true;
    if(strcmp(DecodeEntryTransfer(live,32,actualBase+rva).kind,"none")) { result.reason="entry-branch"; return result; }
    result.delta=actualBase-image.nt.OptionalHeader.ImageBase;
    if(image.nt.OptionalHeader.NumberOfRvaAndSizes>IMAGE_DIRECTORY_ENTRY_BASERELOC) {
        const auto& dir=image.nt.OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_BASERELOC];
        if((dir.VirtualAddress==0)!=(dir.Size==0)) return result;
        const BYTE* reloc=dir.Size?image.At(dir.VirtualAddress,dir.Size):nullptr;
        if(dir.Size && !reloc) return result;
        size_t cursor=0;
        while(cursor<dir.Size) {
            IMAGE_BASE_RELOCATION block={};
            if(dir.Size-cursor<sizeof(block)) return result;
            memcpy(&block,reloc+cursor,sizeof(block));
            if(block.SizeOfBlock<sizeof(block) || block.SizeOfBlock>dir.Size-cursor || (block.SizeOfBlock%4) ||
               (block.VirtualAddress%4096) || block.VirtualAddress>=image.nt.OptionalHeader.SizeOfImage) return result;
            for(size_t pos=sizeof(block);pos<block.SizeOfBlock;pos+=2) {
                WORD item; memcpy(&item,reloc+cursor+pos,2); unsigned type=item>>12;
                if(type==IMAGE_REL_BASED_ABSOLUTE) continue;
                // Unsupported types fail closed, even when outside this window.
                if(type!=IMAGE_REL_BASED_HIGHLOW) return result;
                uint64_t wide=static_cast<uint64_t>(block.VirtualAddress)+(item&4095);
                if(wide+4>image.nt.OptionalHeader.SizeOfImage) return result;
                DWORD at=static_cast<DWORD>(wide);
                if(wide+4<=rva || wide>=static_cast<uint64_t>(rva)+32) continue;
                const BYTE* value=image.At(at,4); if(!value) return result;
                DWORD adjusted; memcpy(&adjusted,value,4); adjusted+=result.delta;
                BYTE bytes[4]; memcpy(bytes,&adjusted,4); ++result.relocations;
                for(unsigned j=0;j<4;++j) if(wide+j>=rva && wide+j<static_cast<uint64_t>(rva)+32) {
                    unsigned k=static_cast<unsigned>(wide+j-rva);
                    if(result.covered[k]) return result; // duplicate/overlapping fixups are ambiguous
                    result.covered[k]=1; result.expected[k]=bytes[j];
                }
            }
            cursor+=block.SizeOfBlock;
        }
    }
    result.normalized=true;
    // No byte is ignored, including relocated operands with incorrect values.
    for(unsigned i=0;i<32;++i) if(live[i]!=result.expected[i] && !result.covered[i]) {
        result.reason="patched-nonrelocation-byte"; return result;
    }
    for(unsigned i=0;i<32;++i) if(live[i]!=result.expected[i]) { result.reason="image-mismatch"; return result; }
    result.reason="verified-system"; return result;
}
