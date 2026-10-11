#pragma once
// M2b: mouse interception while the panel is in "panel mode".
// Design rules (non-negotiable):
//   * The ONLY messages this module ever swallows are (a) a left-button press and
//     its matching release, and (b) a wheel notch, and only when the cursor is
//     inside the panel's own rectangle, plus (c) the numpad keys in panel mode.
//     Everything else - mouse movement above all - is ALWAYS forwarded, so the
//     player keeps the game's camera, aim and unit selection while the panel is
//     up. Consuming movement made panel mode feel like the mouse had died.
//   * Never subclass unless we can prove the current procedure is the one we
//     installed. Never restore over somebody else's subclass: fail closed and log.
//   * Detach() verifies the restore by reading the procedure back.
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include "panel-map.h"

namespace PanelInput {

// The exact Win32 surface this module touches. The product uses the real calls;
// the fixture swaps in fakes so the interception/restore logic is testable in a
// process that has no window station.
struct Win32Api {
    LONG_PTR (*GetProc)(HWND);
    LONG_PTR (*SetProc)(HWND, LONG_PTR);
    BOOL (*IsWindowFn)(HWND);
    LRESULT (*CallProc)(LONG_PTR, HWND, UINT, WPARAM, LPARAM);
    DWORD (*LastErrorFn)();
};
inline LONG_PTR RealGetProc(HWND h) noexcept { return GetWindowLongPtrW(h, GWLP_WNDPROC); }
inline LONG_PTR RealSetProc(HWND h, LONG_PTR p) noexcept { return SetWindowLongPtrW(h, GWLP_WNDPROC, p); }
inline BOOL RealIsWindow(HWND h) noexcept { return IsWindow(h); }
inline LRESULT RealCallProc(LONG_PTR p, HWND h, UINT m, WPARAM w, LPARAM l) noexcept {
    return CallWindowProcW(reinterpret_cast<WNDPROC>(p), h, m, w, l);
}
inline DWORD RealLastError() noexcept { return GetLastError(); }
inline Win32Api RealApi() noexcept { return Win32Api{&RealGetProc,&RealSetProc,&RealIsWindow,&RealCallProc,&RealLastError}; }


// Lives at Header.eventOffset, sizeof 32 bytes, sequence-guarded independently
// of the pixel header so the two writers never disturb each other.
#pragma pack(push, 4)
struct Event {
    DWORD sequence;      // odd while the game writes this block
    DWORD mode;          // observed panel mode (0/1)
    DWORD x, y;          // live cursor position, client/back-buffer space, may be negative
    DWORD left;          // 0/1, left button held
    DWORD wheel;         // accumulated wheel delta since the last host read
    DWORD clicks;        // ++ on every left-button press **in panel mode**; host treats a change as one click
    DWORD clickX, clickY;// position captured at that press (never the later live position)
    DWORD eventAbi;      // M3: layout version for this block (bump if fields change)
    DWORD keyCount;      // M4: ++ for every panel-navigation key swallowed below
    DWORD lastKey;       // M4: the virtual-key code of that key
    DWORD reserved[1];   // must stay zero
};
#pragma pack(pop)
constexpr DWORD EventAbi=2;
static_assert(sizeof(Event) == 52, "panel event ABI");

typedef void (*LogFn)(const char* line);

class MouseHook {
public:
    void Init(PanelOsd::Header* header, BYTE* eventBase, LogFn log) noexcept {
        _header = header; _event = reinterpret_cast<volatile Event*>(eventBase); _log = log;
    }
    // The event block only becomes addressable once the mapping is open, which
    // happens on the first successful panel read. Storing the pointer adds no
    // API call, so the per-frame pipeline contract stays intact.
    void SetEventBase(BYTE* eventBase) noexcept { _event = reinterpret_cast<volatile Event*>(eventBase); }
    bool Active() const noexcept { return _hwnd != nullptr && _old != nullptr; }
    bool Mode() const noexcept { return _mode; }
    // The event block lives inside the host's mapped view. The view can be unmapped
    // (host disabled the panel, heartbeat expiry, reconnect, teardown), and a write
    // through the stale pointer would fault inside the game process - it did
    // (WER: d3d9.dll AV writing _event->sequence). So the pointer is only a valid
    // target while the mapping is alive: product code calls ReleaseEvent() on every
    // path that drops the mapping, and Proc refuses to swallow anything without one.
    bool Armed() const noexcept { return _event != nullptr; }
    void ReleaseEvent() noexcept { _event = nullptr; }
    // Last tracked client-area position (same space as the back buffer, so it can
    // be used as a vertex coordinate directly). Valid in both modes.
    LONG CursorX() const noexcept { return _x; }
    LONG CursorY() const noexcept { return _y; }
    // M3: how many left-button presses have been raised in panel mode. The host
    // acts when this changes; the position to use is the one stored with the press.
    DWORD Clicks() const noexcept { return _clicks; }
    DWORD KeyCount() const noexcept { return _keyCount; }
    DWORD LastKey() const noexcept { return _lastKey; }
    // Reads the device focus window from CreateDevice, so it exists before any
    // panel frame. Only ever called from the Present thread, which already owns
    // the device; the window belongs to the game, never to us.
    const char* FailureReason() const noexcept { return _failure; }
    bool Attach(HWND hwnd) noexcept {
        if (_hwnd) { _failure="already-attached"; return false; }
        if (!hwnd) { _failure="no-window"; return false; }
        if (!_api.IsWindowFn(hwnd)) { _failure="window-gone"; return false; }
        if (!_event) { _failure="event-block-missing"; return false; }
        _old = reinterpret_cast<WNDPROC>(_api.GetProc(hwnd));
        if (!_old) { _failure="no-previous-procedure"; return false; }
        // The window procedure link is the only window field we ever touch;
        // GWLP_USERDATA is deliberately left alone because the game may own it.
        SetLastError(0);
        const LONG_PTR previous = _api.SetProc(hwnd, reinterpret_cast<LONG_PTR>(&MouseHook::Proc));
        if (!previous && _api.LastErrorFn()) { _old = nullptr; _failure="set-window-long-failed"; return false; }
        if (reinterpret_cast<WNDPROC>(previous) != _old) {
            // Somebody changed it between read and write: put it back and give up.
            _api.SetProc(hwnd, previous);
            _old = nullptr; _failure="procedure-changed-during-attach"; return false;
        }
        _hwnd = hwnd;
        g_instance = this;
        _failure=nullptr;
        Log("osd-input-attached mouse-only");
        return true;
    }
    // Verified teardown. Returns true only when the original procedure is back.
    bool Detach() noexcept {
        if (!_hwnd) return true;
        bool restored = false;
        if (!_api.IsWindowFn(_hwnd)) {
            // The game destroyed its window: nothing left to restore, and the
            // reference is gone with it. Report it honestly.
            Log("osd-input-detach-window-gone");
            restored = true;
        } else {
            SetLastError(0);
            const WNDPROC current = reinterpret_cast<WNDPROC>(_api.GetProc(_hwnd));
            if (current != &MouseHook::Proc) {
                // Someone subclassed after us. Never stomp on their procedure.
                Log("osd-input-detach-refused-foreign-subclass");
                restored = false;
            } else {
                SetLastError(0);
                const LONG_PTR previous = _api.SetProc(_hwnd, reinterpret_cast<LONG_PTR>(_old));
                restored = previous == reinterpret_cast<LONG_PTR>(&MouseHook::Proc) && !_api.LastErrorFn();
                if (restored) {
                    SetLastError(0);
                    const WNDPROC check = reinterpret_cast<WNDPROC>(_api.GetProc(_hwnd));
                    restored = check == _old && !_api.LastErrorFn();
                }
                Log(restored ? "osd-input-detached verified" : "osd-input-detach-verify-failed");
            }
        }
        if (g_instance == this) g_instance = nullptr;
        _hwnd = nullptr; _old = nullptr;
        return restored;
    }
    // Called once per panel frame on the Present thread.
    void Sync(bool panelMode) noexcept {
        if (panelMode == _mode) return;
        _mode = panelMode;
        if (panelMode) {
            if (!Attach(_attachHwnd)) Log("osd-input-attach-failed");
        } else {
            Detach();
            // The press/release pairing is only meaningful inside one panel-mode session.
            // A press we swallowed stays marked until its release; if panel mode were
            // re-entered before that release arrived, a stale flag would make us eat the
            // release of a press the game received while we were detached, leaving the game
            // holding the left button down.
            _swallowUp = false;
            _left = 0;
        }
        Publish();
    }
    void SetGameWindow(HWND hwnd) noexcept { _attachHwnd = hwnd; }
    // Test seam only: never called by the product.
    void UseApi(const Win32Api& api) noexcept { _api = api; }
    DWORD MouseMessagesConsumed() const noexcept { return _consumed; }
    DWORD ForwardedMessages() const noexcept { return _forwarded; }
    const Event* LastEvent() const noexcept { return &_last; }

    // Reentrancy-safe publishing: the game reads the same block.
    void Publish() noexcept {
        if (!_event) return;
        ++_writeSeq;
        _event->sequence = _writeSeq * 2u - 1u;
        MemoryBarrier();
        _event->mode = _mode ? 1u : 0u;
        _event->x = static_cast<DWORD>(_x);
        _event->y = static_cast<DWORD>(_y);
        _event->left = _left;
        _event->wheel = static_cast<DWORD>(_wheel);
        _event->clicks = _clicks;
        _event->clickX = static_cast<DWORD>(_clickX);
        _event->clickY = static_cast<DWORD>(_clickY);
        _event->eventAbi = EventAbi;
        _event->keyCount = _keyCount;
        _event->lastKey = _lastKey;
        for (int i = 0; i < 1; ++i) _event->reserved[i] = 0;
        MemoryBarrier();
        _event->sequence = _writeSeq * 2u;
        // Build the host-side copy from the local fields that were just written, not by
        // re-reading shared memory: on x86 the aligned DWORD loads happen to be atomic, but
        // re-reading is both slower and not guaranteed on other targets.
        _last = Event{_writeSeq * 2u, _mode ? 1u : 0u, static_cast<DWORD>(_x), static_cast<DWORD>(_y), _left,
                      static_cast<DWORD>(_wheel), _clicks, static_cast<DWORD>(_clickX), static_cast<DWORD>(_clickY),
                      EventAbi, _keyCount, _lastKey, {0}};
    }

private:
    // The panel's rectangle lives in the same coordinate space as the tracked cursor
    // (both are target/back-buffer space), so containment is a plain comparison.
    // Reads the host-written header, which is a benignly racy read for one message:
    // a torn or bogus rect means "not ours", which fails open - the game gets the
    // message - never "eat input we cannot account for".
    bool InsidePanel(LONG x, LONG y) const noexcept {
        const PanelOsd::Header* h = _header;
        if (!h) return false;
        const DWORD w = h->width, ht = h->height;
        if (!w || !ht || w > PanelOsd::MaxDimension || ht > PanelOsd::MaxDimension) return false;
        // Width/height are bounded by MaxDimension, but the panel origin is a signed value
        // read racily from the host and the header explicitly allows negative coordinates:
        // computing px+w in 32-bit would overflow for an extreme origin (UB, and the
        // inverted comparison would then swallow a click that is not inside the panel).
        const int64_t px = static_cast<LONG>(h->x);
        const int64_t py = static_cast<LONG>(h->y);
        const int64_t cx = x, cy = y;
        return cx >= px && cx < px + static_cast<int64_t>(w) &&
               cy >= py && cy < py + static_cast<int64_t>(ht);
    }
    // The panel's own key set: the classic numpad cross plus the value keys.
    static bool IsPanelKey(UINT vk) noexcept {
        if (vk >= VK_NUMPAD0 && vk <= VK_NUMPAD9) return true;
        switch (vk) {
        case VK_ADD: case VK_SUBTRACT: case VK_MULTIPLY: case VK_DIVIDE:
        case VK_DECIMAL: case VK_SEPARATOR:
            return true;
        default: return false;
        }
    }
    static LRESULT CALLBACK Proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) noexcept {
        // A single process-wide instance: never touch GWLP_USERDATA, which the
        // game may own.
        MouseHook* self = g_instance;
        if (!self || !self->_old) return DefWindowProcW(hwnd, msg, wp, lp);
        // A hook with no live event block swallows nothing and publishes nothing: the
        // block lives in the host's view, so writing through it after the view is gone
        // faults inside the game. Fail closed: forward the game's message untouched.
        if (self->_hwnd == hwnd && self->_event) {
            switch (msg) {
            case WM_MOUSEMOVE:
            case WM_LBUTTONDOWN: case WM_LBUTTONUP:
            case WM_MOUSEWHEEL: {
                // Tracking, forwarding and claiming are three separate things: the
                // position is always recorded and the message is always forwarded, so
                // the game keeps its camera, aim and unit selection and our own drawn
                // cursor sits in the right place the instant panel mode turns on.
                self->_x = static_cast<LONG>(static_cast<short>(LOWORD(lp)));
                self->_y = static_cast<LONG>(static_cast<short>(HIWORD(lp)));
                // Panel mode claims only what belongs to the panel: a press or a wheel
                // notch that lands inside the panel's rectangle. A release follows the
                // press it belongs to, wherever the cursor has moved since.
                bool swallow = false;
                switch (msg) {
                case WM_LBUTTONDOWN: {
                    self->_left = 1;
                    swallow = self->_mode && self->InsidePanel(self->_x, self->_y);
                    // The release follows *this* press: a press the game received must
                    // never be followed by a release we eat (that would leave the game
                    // holding a button down forever), and a press we ate must not leak
                    // a release into the game.
                    self->_swallowUp = swallow;
                    if (swallow) {
                        ++self->_clicks; self->_clickX = self->_x; self->_clickY = self->_y;
                        // Step-named evidence: without this line there is no way to tell
                        // "the press never arrived" from "the host dropped it".
                        char click[128];
                        snprintf(click, sizeof(click), "osd-input-click clicks=%lu x=%ld y=%ld",
                                 static_cast<unsigned long>(self->_clicks), self->_clickX, self->_clickY);
                        self->Log(click);
                    }
                    break;
                }
                case WM_LBUTTONUP:
                    self->_left = 0;
                    swallow = self->_mode && self->_swallowUp;
                    self->_swallowUp = false;
                    break;
                case WM_MOUSEWHEEL: {
                    // The wheel is only ours inside the panel (list paging); outside it
                    // is the game's zoom, so the delta must not be accumulated either.
                    const bool inside = self->_mode && self->InsidePanel(self->_x, self->_y);
                    if (inside) self->_wheel += static_cast<short>(HIWORD(wp));
                    swallow = inside;
                    break;
                }
                default: break;   // WM_MOUSEMOVE is never swallowed
                }
                self->Publish();
                if (swallow) {
                    ++self->_consumed;
                    return 0;
                }
                break;
            }
            default: break;
            }
            // M4: panel-navigation keys are swallowed only while panel mode is on, and
            // only the numpad set. Everything else (WASD, arrows, control groups, F-keys)
            // keeps flowing to the game untouched.
            if (self->_mode && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && IsPanelKey(static_cast<UINT>(wp))) {
                ++self->_keyCount;
                self->_lastKey = static_cast<DWORD>(wp);
                self->Publish();
                return 0;
            }
        }
        if (self->_hwnd == hwnd) ++self->_forwarded;
        return self->_api.CallProc(reinterpret_cast<LONG_PTR>(self->_old), hwnd, msg, wp, lp);
    }
    void Log(const char* line) noexcept { if (_log) _log(line); }

    PanelOsd::Header* _header = nullptr;
    volatile Event* _event = nullptr;
    LogFn _log = nullptr;
    HWND _attachHwnd = nullptr;
    HWND _hwnd = nullptr;
    WNDPROC _old = nullptr;
    bool _mode = false;
    // M7: a release is swallowed only when its own press was swallowed.
    bool _swallowUp = false;
    DWORD _writeSeq = 0;
    LONG _x = 0, _y = 0;
    DWORD _left = 0, _wheel = 0;
    DWORD _clicks = 0;
    DWORD _keyCount = 0, _lastKey = 0;
    LONG _clickX = 0, _clickY = 0;
    Win32Api _api = RealApi();
    const char* _failure = "not-attempted";
    DWORD _consumed = 0, _forwarded = 0;
    Event _last{};
    static MouseHook* g_instance;

};

MouseHook* MouseHook::g_instance = nullptr;

} // namespace PanelInput
