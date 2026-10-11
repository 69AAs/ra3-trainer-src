#pragma once
#include <windows.h>
#include <d3d9.h>
// Offline cooperative ABI v1. Host must hold its device-call gate for ALL
// lifecycle calls, including cached function pointers, Release and Reset.
// Stop(FALSE) disables only; Stop(TRUE) requires that external proof.
// Includes all devices/aliases sharing Present/Reset code, not just this device.
// Never directly write supplied device object memory, vptr, or original vtable.
// All four function pointers below come from GetProcAddress on the resident DLL and
// stay callable for the process lifetime; they do NOT become invalid on Stop(FALSE).
// After a successful Stop(TRUE) (which the product never accepts: overlay.cpp forces
// hostQuiescent=FALSE and refuses) the device is released and the pointers must not be
// cached across Reset/uninstall. Keep them consistent with the noexcept exports.
struct OverlayStats { LONG enabled, callbacks, frames, resets; HRESULT error; };
// ABI v1: five 32-bit words. Host and DLL are built from this same header, so pin the
// layout instead of leaving field interpretation to whichever compiler read it.
static_assert(sizeof(OverlayStats)==20,"OverlayStats ABI v1 must stay 5 x 32-bit words");
typedef BOOL (__cdecl *StartOverlay)(IDirect3DDevice9*);
typedef BOOL (__cdecl *StopOverlay)(BOOL);
typedef void (__cdecl *ReadOverlay)(OverlayStats*);
typedef void (__cdecl *FailOverlay)();
