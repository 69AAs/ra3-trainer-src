// Offline fixture: invokes the real producer formatter, never loads a game.
#include "product-proxy.cpp"
extern "C" BOOL __cdecl OverlayStart(IDirect3DDevice9*) noexcept { return FALSE; }
int wmain() {
    BYTE* page=static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE));
    if(!page) return 1;
    memset(page,0x90,32); page[0]=0xe9; memset(page+1,0,4);
    char line[4096]; ProductFormatEntry("Reset",page,line,sizeof(line));
    char expected[128]; const int expectedLength=snprintf(expected,sizeof(expected),"osd-entry slot=Reset bytes=E9000000009090909090909090909090 target=0x%08lX",static_cast<unsigned long>(reinterpret_cast<uintptr_t>(page)+5));
    if(expectedLength<=0 || expectedLength>=static_cast<int>(sizeof(expected))) { VirtualFree(page,0,MEM_RELEASE); return 1; }
    if(strncmp(line,expected,static_cast<size_t>(expectedLength)) || !strstr(line," targetModule=unidentified reason=unidentified ")) { VirtualFree(page,0,MEM_RELEASE); return 2; }
    puts("FIXTURE (allocated page; not host Reset evidence)"); puts(line);
    ProductFormatEntry("Reset",nullptr,line,sizeof(line));
    if(!strstr(line,"bytes=unreadable target=0x00000000 targetModule=unidentified reason=unidentified ")) { VirtualFree(page,0,MEM_RELEASE); return 3; }
    puts(line); VirtualFree(page,0,MEM_RELEASE);
    puts("EntryFormatTests: 2 passed, 0 skipped"); return 0;
}
