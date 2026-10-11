#include <windows.h>
#include <stdio.h>
int wmain() {
    HMODULE m=LoadLibraryW(L"d3d9.dll");
    if (!m) { printf("load error=%lu\n",GetLastError()); return 1; }
    wchar_t path[MAX_PATH]={}; const DWORD nameLen=GetModuleFileNameW(m,path,MAX_PATH);
    auto marker=reinterpret_cast<DWORD(__cdecl*)()>(GetProcAddress(m,"Round21Marker"));
    bool local=marker && marker()==21;
    printf("x86=%d local-marker=%d\n",sizeof(void*)==4,local);
    // A truncated/failed name would otherwise be printed as an empty path and mislead the probe.
    if (nameLen && nameLen<MAX_PATH) wprintf(L"loaded=%ls\n",path); else wprintf(L"loaded=<unavailable>\n");
    FreeLibrary(m); return local?0:1;
}
