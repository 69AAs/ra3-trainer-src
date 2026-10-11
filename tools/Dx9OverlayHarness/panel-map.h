#pragma once
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

namespace PanelOsd {
constexpr DWORD Magic=0x374c4e50,Abi=1,HeaderSize=128,MaxDimension=512;
constexpr DWORD PixelCapacity=512*512*4,EventReserve=4096,MappingSize=HeaderSize+PixelCapacity+EventReserve;
struct Header {
    DWORD magic,size,abi,sequence,targetPid,hostPid,heartbeat,enabled;
    DWORD width,height,stride,format,generation,x,y,pixelOffset,eventOffset,eventBytes,pixelBytes,checksum;
    ULONGLONG epoch;
    DWORD reserved[10]; // [0]=panelMode (0/1, M2a); [1..9]=ACK/event counters for M2/M3, must stay zero.
};
static_assert(sizeof(Header)==HeaderSize && offsetof(Header,epoch)==80,"panel ABI offsets");
inline bool PanelMode(const Header& h) noexcept { return h.reserved[0]==1; }
inline DWORD Checksum(const BYTE* pixels,DWORD count) noexcept {
    DWORD hash=2166136261u;
    for(DWORD i=0;i<count;++i) hash=(hash^pixels[i])*16777619u;
    return hash;
}
inline const char* Validate(const Header& h,DWORD pid,DWORD now) noexcept {
    if(h.magic!=Magic || h.size!=MappingSize || h.abi!=Abi || h.pixelOffset!=HeaderSize ||
        h.eventOffset!=HeaderSize+PixelCapacity || h.eventBytes!=EventReserve) return "abi-mismatch";
    if(h.targetPid!=pid || !h.hostPid || !h.epoch) return "identity-mismatch";
    if(static_cast<DWORD>(now-h.heartbeat)>=5000) return "heartbeat-expired";
    if(!h.enabled) return "disabled";
    if(h.enabled!=1 || !h.generation || !h.width || !h.height || h.width>MaxDimension || h.height>MaxDimension ||
        h.stride!=h.width*4 || h.pixelBytes!=h.stride*h.height || h.x>8192 || h.y>8192) return "bounds-invalid";
    if(h.format!=1) return "format-unsupported";
    if(h.reserved[0]>1) return "mode-invalid";
    for(int i=1;i<10;++i) if(h.reserved[i]) return "reserved-nonzero";
    return nullptr;
}
struct Reader {
    HANDLE mapping=nullptr;
    const BYTE* view=nullptr;
    DWORD retry=0;
    Header header={};
    BYTE pixels[PixelCapacity]={};
    const char* reason="mapping-missing";
    // A disabled/expired host is not a reason to free the view immediately: the input
    // hook still points into it. The release is deferred until the caller (which knows
    // whether the hook was let go) commits it. See CommitPendingRelease().
    bool pendingRelease=false;
    // M2b: the event block lives inside the same mapping and is written by the
    // game side. Pixel/header writes stay host-only by convention and are
    // still validated on every read.
    BYTE* EventBase() const noexcept { return view ? const_cast<BYTE*>(view)+header.eventOffset : nullptr; }
    const BYTE* View() const noexcept { return view; }
    void Close() noexcept {
        if(view) UnmapViewOfFile(view);
        if(mapping) CloseHandle(mapping);
        view=nullptr;mapping=nullptr;header={};pendingRelease=false;
    }
    // Unmaps only when a previous read asked for it. The product calls this after the
    // input hook has released the event block, so no window message can still write here.
    void CommitPendingRelease() noexcept { if(pendingRelease) Close(); }
    // S_FALSE is an unavailable/disabled/torn snapshot: no draw, retry on later Present.
    HRESULT Read() noexcept {
        const DWORD now=GetTickCount();
        if(!view) {
            if(static_cast<DWORD>(now-retry)<500) { reason="mapping-missing";return S_FALSE; }
            retry=now;
            FILETIME c,e,k,u;
            if(!GetProcessTimes(GetCurrentProcess(),&c,&e,&k,&u)) {reason="process-identity";return E_FAIL;}
            const ULONGLONG created=(static_cast<ULONGLONG>(c.dwHighDateTime)<<32)|c.dwLowDateTime;
            WCHAR name[96];swprintf(name,96,L"Local\\Ra3Trainer.Panel.v1.%lu.%016llX",GetCurrentProcessId(),created);
            mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
            if(!mapping) {reason="mapping-missing";return S_FALSE;}
            view=static_cast<const BYTE*>(MapViewOfFile(mapping,FILE_MAP_READ|FILE_MAP_WRITE,0,0,MappingSize));
            if(!view) {Close();reason="mapping-size";return E_FAIL;}
        }
        const auto shared=reinterpret_cast<const Header*>(view);
        const DWORD first=*reinterpret_cast<const volatile DWORD*>(&shared->sequence);
        if(first&1) {reason="snapshot-busy";return S_FALSE;}
        MemoryBarrier(); Header next={}; memcpy(&next,view,sizeof(next)); MemoryBarrier();
        if(first!=*reinterpret_cast<const volatile DWORD*>(&shared->sequence) || first!=next.sequence) {reason="snapshot-torn";return S_FALSE;}
        reason=Validate(next,GetCurrentProcessId(),now);
        if(reason) {
            // Disabled/expired drops the mapping in the product's own time: the input
            // hook may still hold an event pointer into this view, so unmapping here
            // (under a window message) is exactly the use-after-unmap that crashed the
            // game. Mark it and let the caller release after it let the hook go.
            if(!strcmp(reason,"disabled") || !strcmp(reason,"heartbeat-expired")) {pendingRelease=true;return S_FALSE;}
            return E_INVALIDARG;
        }
        pendingRelease=false;
        const bool dirty=next.epoch!=header.epoch || next.generation!=header.generation;
        if(dirty) memcpy(pixels,view+HeaderSize,next.pixelBytes);
        MemoryBarrier();
        if(first!=*reinterpret_cast<const volatile DWORD*>(&shared->sequence)) {header={};reason="snapshot-torn";return S_FALSE;}
        if(dirty && Checksum(pixels,next.pixelBytes)!=next.checksum) { header={};reason="checksum-mismatch";return E_INVALIDARG; }
        // Stable generation must not silently change shape, position or pixel identity.
        if(!dirty && (next.width!=header.width || next.height!=header.height || next.x!=header.x || next.y!=header.y || next.checksum!=header.checksum)) {
            reason="generation-mismatch";return E_INVALIDARG;
        }
        header=next;reason=nullptr;return S_OK;
    }
};
}
