// aob-scan.h - 特征码（AOB）扫描：把"写死地址"换成"扫出地址"
//
// 为什么需要它：写死的 RVA 只要游戏/加载器一改就废，而"一串字节特征"通常在多次重建之间保持稳定。
// 这是 2026 年的标准做法（Reloaded-II 官方文档整章讲它；OpenVHook 靠同一份二进制支持 69 个游戏版本）。
//
// 纪律：
//   * **只接受唯一命中**。多个命中一律拒绝（宁可回退到旧地址，也不猜）——猜错 = 把用户的游戏打崩。
//   * 只读：全程用 CodeHook::Read，不写目标进程内存。
//   * 不写死段大小：从 PE 头里读 .text 的 VA/大小。
#pragma once
#include "code-hook.h"
#include <string.h>

namespace AobScan {
    // 掩码约定：0xFF = 该字节必须完全相等；0x00 = 通配（任意字节）。
    inline const unsigned char* Scan(const unsigned char* begin, size_t size,
                                     const unsigned char* pattern, const unsigned char* mask,
                                     size_t patternSize) {
        if (begin == nullptr || pattern == nullptr || patternSize == 0 || size < patternSize) return nullptr;
        const size_t last = size - patternSize;
        for (size_t i = 0; i <= last; ++i) {
            bool hit = true;
            for (size_t j = 0; j < patternSize; ++j) {
                if (mask != nullptr && mask[j] == 0x00) continue;
                if (begin[i + j] != pattern[j]) { hit = false; break; }
            }
            if (hit) return begin + i;
        }
        return nullptr;
    }

    // 唯一命中才返回；0 个或 >=2 个都返回 nullptr。
    inline const unsigned char* ScanUnique(const unsigned char* begin, size_t size,
                                           const unsigned char* pattern, const unsigned char* mask,
                                           size_t patternSize) {
        const unsigned char* first = Scan(begin, size, pattern, mask, patternSize);
        if (first == nullptr) return nullptr;
        const unsigned char* rest = first + 1;
        const size_t restSize = size - static_cast<size_t>(rest - begin);
        if (Scan(rest, restSize, pattern, mask, patternSize) != nullptr) return nullptr;   // 不唯一 ⇒ 拒绝
        return first;
    }

    struct Range {
        const unsigned char* base;
        size_t size;
        bool ok;
    };

    // 从 PE 头定位 .text（读失败 ⇒ ok=false，调用方必须回退，不许硬上）
    inline Range TextSection(void* moduleBase) {
        Range result = {nullptr, 0, false};
        if (moduleBase == nullptr) return result;
        unsigned char* base = static_cast<unsigned char*>(moduleBase);

        unsigned char dos[0x40] = {};
        if (!CodeHook::Read(base, dos, sizeof(dos))) return result;
        if (dos[0] != 'M' || dos[1] != 'Z') return result;

        unsigned lfanew = 0;
        memcpy(&lfanew, dos + 0x3C, sizeof(lfanew));
        if (lfanew == 0 || lfanew > 0x1000) return result;

        unsigned char pe[24] = {};
        if (!CodeHook::Read(base + lfanew, pe, sizeof(pe))) return result;
        if (pe[0] != 'P' || pe[1] != 'E' || pe[2] != 0 || pe[3] != 0) return result;

        unsigned short sections = 0, optionalSize = 0;
        memcpy(&sections, pe + 6, sizeof(sections));
        memcpy(&optionalSize, pe + 20, sizeof(optionalSize));
        const unsigned tableAt = lfanew + 24u + optionalSize;

        for (unsigned i = 0; i < sections && i < 64; ++i) {
            unsigned char head[40] = {};
            if (!CodeHook::Read(base + tableAt + i * 40u, head, sizeof(head))) return result;
            if (memcmp(head, ".text", 5) != 0) continue;
            unsigned virtualSize = 0, virtualAddress = 0;
            memcpy(&virtualSize, head + 8, sizeof(virtualSize));
            memcpy(&virtualAddress, head + 12, sizeof(virtualAddress));
            if (virtualSize == 0) return result;
            result.base = base + virtualAddress;
            result.size = virtualSize;
            result.ok = true;
            return result;
        }
        return result;
    }

    // 在 .text 里按特征码定位；找不到/不唯一 ⇒ nullptr（失败关闭）
    inline void* FindInText(void* moduleBase, const unsigned char* pattern,
                            const unsigned char* mask, size_t patternSize) {
        const Range text = TextSection(moduleBase);
        if (!text.ok) return nullptr;
        return const_cast<unsigned char*>(
            ScanUnique(text.base, text.size, pattern, mask, patternSize));
    }
}
