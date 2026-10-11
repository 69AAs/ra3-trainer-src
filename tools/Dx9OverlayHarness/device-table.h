#pragma once
#include <stddef.h>
#include <stdio.h>
#include <string.h>

// Plain IDirect3DDevice9 only; abi-probe.cpp binds these to d3d9.h.
constexpr size_t DeviceSlots=119;
constexpr size_t PresentSlot=17;
constexpr size_t ClearSlot=43;

template<size_t N>
bool ValidateDeviceTable(void* const* source,void* const (&copy)[N],void* present) {
    if (!source || !present || N!=DeviceSlots) {
        printf("vtable rejected: source=%p replacement=%p slots=%u expected=%u\n",
            static_cast<const void*>(source),present,static_cast<unsigned>(N),static_cast<unsigned>(DeviceSlots));
        return false;
    }
    for (size_t i=0;i<N;i++) {
        if (!source[i] || !copy[i] || copy[i]!=(i==PresentSlot?present:source[i])) {
            printf("vtable rejected: slot=%u source=%p copy=%p\n",static_cast<unsigned>(i),source[i],copy[i]);
            return false;
        }
    }
    return true;
}

template<size_t N>
bool PrepareDeviceTable(void* const* source,void* (&copy)[N],void* present) {
    // Reject a short destination BEFORE copying, including accidental pointer-size copies.
    if (!source || !present || N!=DeviceSlots) return ValidateDeviceTable(source,copy,present);
    memcpy(copy,source,sizeof(copy));
    copy[PresentSlot]=present;
    // Validate the patched candidate, including original Present, before publishing it.
    return ValidateDeviceTable(source,copy,present);
}
