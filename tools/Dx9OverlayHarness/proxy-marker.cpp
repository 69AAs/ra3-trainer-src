#include <windows.h>
// Loader-search sentinel, NOT a D3D9 implementation. Isolated probe dir only.
extern "C" __declspec(dllexport) DWORD __cdecl Round21Marker() { return 21; }
BOOL WINAPI DllMain(HINSTANCE,DWORD,LPVOID) { return TRUE; }
