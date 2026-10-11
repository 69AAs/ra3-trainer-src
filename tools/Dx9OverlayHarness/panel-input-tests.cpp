// Mechanic fixture for panel-input.h. Uses a fake Win32 window table, so it
// needs no window station, never creates a window and never touches a game.
#include "panel-input.h"
#include <stdio.h>
#include <string.h>

static int checks=0, failures=0;
static void Check(bool ok,const char* name){ ++checks; if(!ok)++failures; printf("%s %s\n", ok?"PASS":"FAIL", name); }

static int forwarded=0;                       // messages that reached the game's procedure
static LRESULT CALLBACK GameProc(HWND h,UINT m,WPARAM w,LPARAM l){ (void)h;(void)w;(void)l; ++forwarded; return (LRESULT)m; }
static LRESULT CALLBACK ForeignProc(HWND h,UINT m,WPARAM w,LPARAM l){ (void)h;(void)m;(void)w;(void)l; return 0; }

struct FakeWindow { LONG_PTR proc; bool alive; bool setFails; };
static FakeWindow windows[4] = {};
static DWORD fakeLastError = 0;   // only set when the fake refuses a write

static LONG_PTR FakeGetProc(HWND h){ return windows[(intptr_t)h].proc; }
static LONG_PTR FakeSetProc(HWND h,LONG_PTR p){ auto& w=windows[(intptr_t)h]; if(w.setFails){ fakeLastError=ERROR_ACCESS_DENIED; return 0; } LONG_PTR old=w.proc; w.proc=p; fakeLastError=0; return old; }
static BOOL FakeIsWindow(HWND h){ return windows[(intptr_t)h].alive; }
static LRESULT FakeCallProc(LONG_PTR p,HWND h,UINT m,WPARAM w,LPARAM l){ return reinterpret_cast<WNDPROC>(p)(h,m,w,l); }
// Reports a real error only for the refused write, so the "set-window-long-failed" branch
// (and Detach's !LastErrorFn() verification) is actually reachable instead of always false.
static DWORD FakeLastError(){ return fakeLastError; }
static PanelInput::Win32Api FakeApi(){ return PanelInput::Win32Api{&FakeGetProc,&FakeSetProc,&FakeIsWindow,&FakeCallProc,&FakeLastError}; }

static void Log(const char* line){ printf("   log: %s\n", line); }
static int Probe(HWND h,UINT m,WPARAM w,LPARAM l){ return (int)FakeCallProc(FakeGetProc(h),h,m,w,l); }

int wmain() {
    windows[1] = FakeWindow{ reinterpret_cast<LONG_PTR>(&GameProc), true, false };
    HWND game = reinterpret_cast<HWND>(1);

    BYTE* eventPage = static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    BYTE* headerPage = static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
    Check(eventPage && headerPage,"fixture pages allocated"); if(!eventPage||!headerPage) return 2;
    memset(eventPage,0,4096); memset(headerPage,0,4096);

    PanelInput::MouseHook hook;
    hook.UseApi(FakeApi());
    hook.Init(reinterpret_cast<PanelOsd::Header*>(headerPage),eventPage,Log);
    hook.SetGameWindow(game);
    // The panel's rectangle is what decides whether a click belongs to the panel.
    // It lives in the same coordinate space as the tracked cursor (both are
    // target/back-buffer space) and is published by the host before every frame.
    // Capped at MaxDimension (512) exactly like the host validates it.
    auto* header = reinterpret_cast<PanelOsd::Header*>(headerPage);
    header->x = 0; header->y = 0; header->width = 512; header->height = 512;
    Check(!hook.Active() && hook.Detach(),"inactive hook detaches trivially");
    Check(!hook.Attach(nullptr),"null window rejected");
    Check(!hook.Attach(reinterpret_cast<HWND>(2)),"non-window rejected");
    Check(hook.Attach(game) && hook.Active(),"attach subclasses the game window");

    // Mode off: nothing is intercepted.
    forwarded=0; Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(11,22)); Probe(game,WM_KEYDOWN,VK_F9,0);
    Check(forwarded==2 && hook.MouseMessagesConsumed()==0,"mode off: every message forwarded, none consumed");

    // Mode on: movement keeps reaching the game (the camera, aim and unit
    // selection must survive); only a press or wheel landing inside the panel's
    // rectangle belongs to the panel. Keyboard/system still forward.
    hook.Sync(true);
    Check(FakeGetProc(game)!=reinterpret_cast<LONG_PTR>(&GameProc),"panel mode installed our procedure");
    forwarded=0; Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(101,202));
    Check(forwarded==1 && hook.MouseMessagesConsumed()==0,"panel mode: mouse movement is forwarded, never consumed");
    Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(101,202));
    Probe(game,WM_LBUTTONUP,0,MAKELPARAM(101,202));
    Check(hook.MouseMessagesConsumed()==2 && forwarded==1,"panel mode: a click inside the panel is consumed (press and release)");
    Probe(game,WM_KEYDOWN,VK_F9,0); Probe(game,WM_KEYUP,VK_F9,0);
    Probe(game,WM_SIZE,SIZE_RESTORED,MAKELPARAM(1280,720)); Probe(game,WM_ACTIVATE,WA_ACTIVE,0);
    Check(forwarded==5,"panel mode: keyboard, size and activation always forwarded");
    Probe(game,WM_RBUTTONDOWN,0,MAKELPARAM(5,6));
    Check(forwarded==6,"panel mode: right button still reaches the game");

    const volatile PanelInput::Event* ev = reinterpret_cast<const volatile PanelInput::Event*>(eventPage);
    Check((ev->sequence%2)==0 && ev->sequence>0,"event block published with stable even sequence");
    Check(ev->mode==1 && ev->x==101 && ev->y==202 && ev->left==0,"event carries mode and cursor position");
    Check(ev->reserved[0]==0,"event reserve stays zero for M2c/M3");

    // Leaving panel mode restores the original procedure, verified by read-back.
    hook.Sync(false);
    Check(!hook.Active(),"mode off detaches the hook");
    Check(FakeGetProc(game)==reinterpret_cast<LONG_PTR>(&GameProc),"detach restored the original procedure");
    forwarded=0; Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(1,1));
    Check(forwarded==1,"after detach the mouse reaches the game again");
    Check(ev->mode==0,"detach publishes mode 0 to the host");

    // A foreign subclass installed after us must never be stomped on.
    Check(hook.Attach(game),"re-attach for the foreign-subclass case");
    FakeSetProc(game,reinterpret_cast<LONG_PTR>(&ForeignProc));
    Check(!hook.Detach(),"foreign subclass refused: fail closed, no stomp");
    Check(FakeGetProc(game)==reinterpret_cast<LONG_PTR>(&ForeignProc),"the foreign procedure was left untouched");
    FakeSetProc(game,reinterpret_cast<LONG_PTR>(&GameProc));

    // SetWindowLongPtr failing must not leave us believing we hooked anything.
    windows[1].setFails=true;
    Check(!hook.Attach(game),"attach fails closed when the OS refuses the write");
    windows[1].setFails=false;

    // A destroyed window cannot be restored: report honestly, do not crash.
    Check(hook.Attach(game),"attach before the window dies");
    windows[1].alive=false;
    Check(hook.Detach(),"destroyed window: detach reports honestly without crashing");

    windows[1] = FakeWindow{ reinterpret_cast<LONG_PTR>(&GameProc), true, false };

    // ---- M3: click events ----
    {
        const PanelInput::Event* e = reinterpret_cast<const PanelInput::Event*>(eventPage);
        Check(hook.Attach(game),"M3 re-attach for click tests");
        hook.Sync(false);
        const DWORD beforeOff = e->clicks;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,(50u<<16)|100u);
        Check(e->clicks==beforeOff,"M3 mode off: a left press must not raise a click");
        hook.Sync(true);
        const DWORD base = e->clicks;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,(50u<<16)|100u);
        Check(e->clicks==base+1,"M3 mode on: a left press raises exactly one click");
        Check(e->clickX==100 && e->clickY==50,"M3 the click carries the position captured at the press");
        Probe(game,WM_MOUSEMOVE,0,(900u<<16)|300u);
        Check(e->clickX==100 && e->clickY==50,"M3 the click position does not drift when the mouse keeps moving");
        Check(e->x==300 && e->y==900,"M3 the live cursor position still tracks the mouse");
        Probe(game,WM_LBUTTONUP,0,(900u<<16)|300u);
        Check(e->clicks==base+1,"M3 releasing the button must not raise a second click");
        Probe(game,WM_MOUSEWHEEL,0,0);
        Probe(game,WM_MOUSEMOVE,0,(60u<<16)|110u);
        Check(e->clicks==base+1,"M3 moves and wheel never raise clicks");
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,(10u<<16)|20u);
        Check(e->clicks==base+2,"M3 a second press raises the second click");
        Check(e->clickX==20 && e->clickY==10,"M3 the second click carries its own position");
        Check(e->eventAbi==PanelInput::EventAbi,"M3 the event block declares its layout version");
        Check(sizeof(PanelInput::Event)==52 && e->reserved[0]==0,
              "M3 event block keeps its ABI size with the reserve still zeroed");
        hook.Sync(false);
    }

    // ---- M5: 映射消失后的输入钩子（真机 d3d9.dll AV 回归） ----
    // 真机证据：WER 报 d3d9.dll 0xc0000005 @0x1fd08 = MouseHook::Publish 里
    // _event->sequence = _writeSeq*2-1 的写指令；事件块在建图视图里，视图被
    // UnmapViewOfFile 之后指针没松，下一条被吞的鼠标/小键盘消息就写到已释放的页。
    {
        const PanelInput::Event* e = reinterpret_cast<const PanelInput::Event*>(eventPage);
        hook.Sync(true);
        Check(hook.Active() && hook.Armed() && hook.Mode(),"M5 panel mode is armed while the mapping is alive");
        forwarded=0; Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(7,8));
        Check(forwarded==0 && hook.MouseMessagesConsumed()>0,"M5 live mapping: a press inside the panel is consumed");

        // 勾子没有事件块时绝不能吞消息（旧行为会吞掉游戏鼠标）。
        const DWORD consumedBefore=hook.MouseMessagesConsumed();
        const DWORD keyCountBefore=hook.KeyCount();
        const DWORD seqBefore=e->sequence;
        hook.SetEventBase(nullptr);
        Check(!hook.Armed(),"M5 a null event base reports not armed");
        forwarded=0;
        Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(9,10));
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(9,10));
        Probe(game,WM_LBUTTONUP,0,MAKELPARAM(9,10));
        Probe(game,WM_MOUSEWHEEL,0,0);
        Probe(game,WM_KEYDOWN,VK_NUMPAD2,0);
        Check(forwarded==5,"M5 unarmed hook forwards every message untouched (never eats the game's input)");
        Check(hook.MouseMessagesConsumed()==consumedBefore,"M5 unarmed hook consumes nothing");
        Check(e->sequence==seqBefore && e->clicks==hook.Clicks(),"M5 unarmed hook never writes the event block");

        // 产品路径：视图即将消失时先 ReleaseEvent()，再让消息进来。
        hook.SetEventBase(eventPage);
        Check(hook.Armed(),"M5 re-arming the event block works (reconnect)");
        forwarded=0; Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(11,12));
        Check(forwarded==0,"M5 re-armed hook consumes again");
        const DWORD consumedLive=hook.MouseMessagesConsumed();
        const DWORD seqArmed=e->sequence;
        hook.ReleaseEvent();
        forwarded=0;
        Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(13,14));
        Probe(game,WM_SYSKEYDOWN,VK_ADD,0);
        Check(forwarded==2 && hook.MouseMessagesConsumed()==consumedLive && e->sequence==seqArmed,
              "M5 after ReleaseEvent nothing is swallowed and nothing is written");

        // 真把视图还回去：消息照样必须只转发，不许崩。
        VirtualFree(eventPage,0,MEM_RELEASE);
        forwarded=0;
        Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(15,16));
        Probe(game,WM_KEYDOWN,VK_NUMPAD8,0);
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(15,16));
        Check(forwarded==3,"M5 with the view actually released the hook still only forwards");
        Check(hook.KeyCount()==keyCountBefore,"M5 a released view can never raise a panel key");

        hook.Sync(false);
        Check(!hook.Active() && !hook.Mode(),"M5 mode off detaches even after the mapping was released");
        Check(FakeGetProc(game)==reinterpret_cast<LONG_PTR>(&GameProc),"M5 detach restored the original procedure after the mapping was released");
    }

    // ---- M7: 面板模式下只有"落在面板矩形里的点击/滚轮"归面板 ----
    // 真机证据：用户一开面板模式游戏里鼠标就像失灵（session-20261003 里 11 次面板外
    // 点击全被吞，移动也全被吞）。移动必须放行，否则相机、瞄准、选兵一起废；面板外
    // 的点击也必须放行，否则点"发兵"前后根本没法打。
    {
        BYTE* page2 = static_cast<BYTE*>(VirtualAlloc(nullptr,4096,MEM_COMMIT|MEM_RESERVE,PAGE_READWRITE));
        Check(page2!=nullptr,"M7 fixture page allocated"); if(!page2) return 2;
        memset(page2,0,4096);
        const PanelInput::Event* e2 = reinterpret_cast<const PanelInput::Event*>(page2);
        hook.SetEventBase(page2);
        header->x = 24; header->y = 86; header->width = 360; header->height = 440;
        hook.Sync(true);
        Check(hook.Active() && hook.Armed(),"M7 panel mode armed with a live event block");

        forwarded=0;
        Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(1000,600));
        Probe(game,WM_MOUSEMOVE,0,MAKELPARAM(100,200));
        Check(forwarded==2,"M7 movement is forwarded inside and outside the panel (the game keeps its camera)");

        const DWORD clicksBase = hook.Clicks();
        forwarded=0;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(1000,600));   // 面板外
        Probe(game,WM_LBUTTONUP,0,MAKELPARAM(1000,600));
        Check(forwarded==2 && hook.Clicks()==clicksBase,
              "M7 a press outside the panel reaches the game and raises no panel click");

        forwarded=0;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(100,200));    // 面板内
        Probe(game,WM_LBUTTONUP,0,MAKELPARAM(100,200));
        Check(forwarded==0 && hook.Clicks()==clicksBase+1,
              "M7 a click inside the panel is consumed and raises exactly one panel click");

        // 按下在面板内、抬起飘到面板外：抬起仍归面板，游戏不该收到"孤儿抬起"。
        forwarded=0;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(100,200));
        Probe(game,WM_LBUTTONUP,0,MAKELPARAM(1600,900));
        Check(forwarded==0 && hook.Clicks()==clicksBase+2,
              "M7 a release follows the press it belongs to, wherever the cursor ended up");

        // 按下在面板外、抬起在面板内：两半都还给游戏。
        forwarded=0;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(1600,900));
        Probe(game,WM_LBUTTONUP,0,MAKELPARAM(100,200));
        Check(forwarded==2 && hook.Clicks()==clicksBase+2,
              "M7 a press outside the panel keeps both halves for the game");

        // 滚轮：面板内 = 面板翻页，面板外 = 游戏缩放。
        forwarded=0;
        Probe(game,WM_MOUSEWHEEL,static_cast<WPARAM>(WHEEL_DELTA<<16),static_cast<LPARAM>((900u<<16)|1600u));
        Check(forwarded==1,"M7 a wheel notch outside the panel is the game's zoom");
        forwarded=0;
        Probe(game,WM_MOUSEWHEEL,static_cast<WPARAM>(WHEEL_DELTA<<16),static_cast<LPARAM>((200u<<16)|100u));
        Check(forwarded==0 && e2->wheel>0,"M7 a wheel notch inside the panel is consumed for list paging");

        // 面板矩形不可信（未发布/被撕裂/超限）时一律放行：宁可漏吞，也不能吃掉游戏的输入。
        forwarded=0;
        header->width = 0;
        Probe(game,WM_LBUTTONDOWN,MK_LBUTTON,MAKELPARAM(100,200));
        Probe(game,WM_LBUTTONUP,0,MAKELPARAM(100,200));
        Probe(game,WM_MOUSEWHEEL,static_cast<WPARAM>(WHEEL_DELTA<<16),static_cast<LPARAM>((200u<<16)|100u));
        Check(forwarded==3,"M7 an unusable panel rect fails open: the game keeps every message");
        header->x = 0; header->y = 0; header->width = 512; header->height = 512;

        // 小键盘归面板，WASD 必须原样留给游戏。
        forwarded=0;
        Probe(game,WM_KEYDOWN,VK_NUMPAD2,0);
        Probe(game,WM_KEYDOWN,'W',0);
        Check(forwarded==1,"M7 in panel mode the numpad is ours and WASD stays the game's");

        hook.Sync(false);
        Check(FakeGetProc(game)==reinterpret_cast<LONG_PTR>(&GameProc),"M7 detach restored the original procedure");
        // The view is about to be unmapped: drop the event pointer first, exactly like the M5
        // block above, so no later message can write through a stale event base.
        hook.ReleaseEvent();
        VirtualFree(page2,0,MEM_RELEASE);
    }

    VirtualFree(headerPage,0,MEM_RELEASE);
    printf("RESULT panel-input checks=%d failures=%d skipped=0\n",checks,failures);
    return failures?1:0;
}
