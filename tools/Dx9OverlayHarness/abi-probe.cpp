#define CINTERFACE
#include <windows.h>
#include <d3d9.h>
#include <stddef.h>
#include <stdio.h>
#include "device-table.h"
static_assert(sizeof(void*)==4,"x86");
static_assert(sizeof(IDirect3DDevice9ExVtbl)==134*sizeof(void*),"documented Ex ABI, not private implementation extent");
static_assert(sizeof(IDirect3DDevice9Vtbl)==119*sizeof(void*),"plain D3D9 vtable");
static_assert(sizeof(IDirect3DDevice9Vtbl)==DeviceSlots*sizeof(void*),"harness table length");
static_assert(offsetof(IDirect3DDevice9Vtbl,Clear)==ClearSlot*sizeof(void*),"Clear ABI");
static_assert(offsetof(IDirect3DDevice9Vtbl,Present)==PresentSlot*sizeof(void*),"harness Present ABI");
static_assert(offsetof(IDirect3DDevice9Vtbl,Reset)==16*sizeof(void*),"Reset ABI");
static_assert(offsetof(IDirect3DDevice9Vtbl,Present)==17*sizeof(void*),"Present ABI");
static_assert(offsetof(IDirect3DDevice9Vtbl,GetCreationParameters)==9*sizeof(void*),"creation ABI");
static_assert(offsetof(IDirect3DDevice9Vtbl,UpdateSurface)==30*sizeof(void*),"copy ABI");
static_assert(offsetof(IDirect3DDevice9Vtbl,CreateOffscreenPlainSurface)==36*sizeof(void*),"surface ABI");
int wmain() {
    // Printed from the validated constants/offsetof values so the prose cannot drift
    // away from what the static_asserts above actually proved.
    printf("x86=%u bytes; IDirect3DDevice9=%u slots/%u bytes; Reset=%u; Present=%u; Clear=%u (offset=%u/0x%x); verified against mingw-w64 d3d9.h\n",
        static_cast<unsigned>(sizeof(void*)),
        static_cast<unsigned>(sizeof(IDirect3DDevice9Vtbl)/sizeof(void*)),
        static_cast<unsigned>(sizeof(IDirect3DDevice9Vtbl)),
        static_cast<unsigned>(offsetof(IDirect3DDevice9Vtbl,Reset)/sizeof(void*)),
        static_cast<unsigned>(offsetof(IDirect3DDevice9Vtbl,Present)/sizeof(void*)),
        static_cast<unsigned>(ClearSlot),
        static_cast<unsigned>(offsetof(IDirect3DDevice9Vtbl,Clear)),
        static_cast<unsigned>(offsetof(IDirect3DDevice9Vtbl,Clear)));
    return 0;
}
