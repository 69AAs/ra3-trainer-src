#pragma once
#include "overlay.h"
#include <stdio.h>
// Scope exists only during the synchronous factory -> Capture -> OverlayStart
// call. No loader/window thread inference and no shared mutable pending device.
struct OverlayCreationCall {
    IDirect3DDevice9* device;
    DWORD tid, requestedFlags;
    D3DDEVTYPE requestedType;
    HWND focusWindow, deviceWindow;
    HRESULT hr;
};
inline thread_local const OverlayCreationCall* overlayCreationCall=nullptr;
inline bool OverlayThreadAllowed(DWORD creationTid,DWORD presentTid,DWORD flags) noexcept {
    return creationTid!=0 && (creationTid==presentTid || (flags&D3DCREATE_MULTITHREADED)!=0);
}
inline void OverlayFormatCreation(char* line,size_t size,const OverlayCreationCall& call,
    const D3DDEVICE_CREATION_PARAMETERS& cp,HRESULT queryHr) noexcept {
    snprintf(line,size,"osd-draw-thread role=CreateDevice tid=%lu flags=0x%08lx hr=%08lx queryHr=%08lx flagsValid=%u DeviceType=%u hFocusWindow=%p hDeviceWindow=%p device=%p source=CreateDevice-entry flagsSource=GetCreationParameters deviceWindowSource=CreateDevice-parameters requestedFlags=0x%08lx requestedType=%u requestedFocus=%p",
        call.tid,cp.BehaviorFlags,static_cast<unsigned long>(call.hr),static_cast<unsigned long>(queryHr),
        SUCCEEDED(queryHr)?1u:0u,static_cast<unsigned>(cp.DeviceType),cp.hFocusWindow,call.deviceWindow,
        call.device,call.requestedFlags,static_cast<unsigned>(call.requestedType),call.focusWindow);
}
