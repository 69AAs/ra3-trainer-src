#include "entry-diagnostics.h"
#include <stdio.h>
static int count,failed;
static void Check(bool b,const char* text) { ++count; if(!b) ++failed; printf("%s %s\n",b?"PASS":"FAIL",text); }
int wmain() {
 BYTE rel[]={0xe9,0xfb,0xff,0xff,0xff}; auto r=DecodeEntryTransfer(rel,5,0x1000); Check(r.resolved && r.target==0x1000,"signed rel32 destination");
 Check(!DecodeEntryTransfer(rel,4,0x1000).resolved,"truncated rel32 is unknown");
 BYTE ind[]={0xff,0x25,0x78,0x56,0x34,0x12}; r=DecodeEntryTransfer(ind,6,0); Check(!r.resolved && r.cell==0x12345678,"indirect needs separate safe read");
 BYTE push[]={0x68,0x78,0x56,0x34,0x12,0xc3}; Check(DecodeEntryTransfer(push,6,0).target==0x12345678,"push-ret destination");
 BYTE mov[]={0xb8,0x78,0x56,0x34,0x12,0xff,0xe0}; Check(DecodeEntryTransfer(mov,7,0).target==0x12345678,"mov-jmp destination");
 // Every prefix needs its own truncation case: the decoder must read only within the
 // supplied snapshot length, which is the injection-safety property this suite pins down.
 Check(!DecodeEntryTransfer(ind,5,0).resolved && DecodeEntryTransfer(ind,5,0).cell==0,"truncated indirect is unknown");
 Check(!DecodeEntryTransfer(push,5,0).resolved,"truncated push-ret is unknown");
 Check(!DecodeEntryTransfer(mov,6,0).resolved,"truncated mov-jmp is unknown");
 BYTE pushWrongTail[]={0x68,0x78,0x56,0x34,0x12,0x90}; Check(!DecodeEntryTransfer(pushWrongTail,6,0).resolved,"push without ret tail is unknown");
 BYTE hot[]={0x8b,0xff,0x55}; Check(!DecodeEntryTransfer(hot,3,0).resolved,"hotpatch prefix not a transfer");
 BYTE shortJump[]={0xeb,0xf9}; Check(DecodeEntryTransfer(shortJump,2,0x1000).target==0xffb,"hotpatch short jump signed destination");
 BYTE b[8]={}; Check(!ReadEntryBytes(nullptr,b,8),"unreadable pointer fails closed");
 Check(ReadEntryBytes(push,b,6) && !memcmp(b,push,6),"safe byte snapshot");
 char hex[13]; EntryHex(push,6,hex); Check(!strcmp(hex,"6878563412C3"),"exact raw hex");
 char name[780]; EntryModuleName(nullptr,name,sizeof(name)); Check(!strcmp(name,"unidentified"),"unknown target attribution");
 EntryModuleName(reinterpret_cast<void*>(&wmain),name,sizeof(name)); Check(strstr(name,"EntryDiagnosticsTests.exe")!=nullptr,"known target module");
 // Capacity contract: a tiny buffer must be bounded and NUL-terminated, never overflowed.
 char tiny[8]; memset(tiny,0x5a,sizeof(tiny));
 EntryModuleName(reinterpret_cast<void*>(&wmain),tiny,sizeof(tiny));
 Check(tiny[sizeof(tiny)-1]==0 && strlen(tiny)<sizeof(tiny),"a tiny buffer is truncated and terminated");
 // Derive the expected leaf name at runtime: a literal would couple this fixture to the
 // build target name, and EntryModuleName sanitizes spaces/'|' into '_'.
 WCHAR self[MAX_PATH]={}; GetModuleFileNameW(nullptr,self,MAX_PATH);
 const WCHAR* leaf=wcsrchr(self,L'\\'); const WCHAR* base=leaf?leaf+1:self; char expected[MAX_PATH*3]={};
 WideCharToMultiByte(CP_UTF8,0,base,-1,expected,sizeof(expected),nullptr,nullptr);
 for(char* c=expected;*c;++c) if(*c=='\r'||*c=='\n'||*c=='|'||*c==' ') *c='_';
 EntryModuleName(reinterpret_cast<void*>(&wmain),name,sizeof(name));
 Check(strcmp(name,expected)==0,"known target module matches the running image name");
 printf("entry diagnostics checks=%d failures=%d\n",count,failed); return failed?1:0;
}
