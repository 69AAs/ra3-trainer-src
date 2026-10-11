// aob-scan-tests.cpp - 证明特征码扫描器的行为：唯一才认、多个/零个一律拒绝、通配符按约定工作。
// 纯内存夹具，不碰游戏、不碰 GPU。
#include "aob-scan.h"
#include <stdio.h>
#include <string.h>

static int checks = 0, failures = 0;
static void Check(bool ok, const char* what) {
    ++checks;
    if (!ok) { ++failures; printf("FAIL %s\n", what); } else { printf("PASS %s\n", what); }
}

int wmain() {
    setvbuf(stdout, nullptr, _IONBF, 0);

    const unsigned char buf[] = {
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x8b, 0x54, 0x24, 0x08, 0x85, 0xd2, 0x56, 0x75,   // 类真实片段
        0x99, 0xAA, 0xBB, 0xCC
    };
    const size_t size = sizeof(buf);

    // 1) 精确匹配：开头 / 中间 / 结尾
    const unsigned char head[] = {0x11, 0x22};
    const unsigned char mid[]  = {0x8b, 0x54, 0x24, 0x08};
    const unsigned char tail[] = {0xBB, 0xCC};
    Check(AobScan::Scan(buf, size, head, nullptr, sizeof(head)) == buf, "match at the start is found");
    Check(AobScan::Scan(buf, size, mid, nullptr, sizeof(mid)) == buf + 8, "match in the middle is found");
    Check(AobScan::Scan(buf, size, tail, nullptr, sizeof(tail)) == buf + size - 2, "match at the very end is found");

    // 2) 不匹配 / 边界
    const unsigned char absent[] = {0xDE, 0xAD, 0xBE, 0xEF};
    Check(AobScan::Scan(buf, size, absent, nullptr, sizeof(absent)) == nullptr, "an absent pattern is not found");
    const unsigned char tooLong[] = {0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x8b, 0x54, 0x24, 0x08,
                                     0x85, 0xd2, 0x56, 0x75, 0x99, 0xAA, 0xBB, 0xCC, 0x01};
    Check(AobScan::Scan(buf, size, tooLong, nullptr, sizeof(tooLong)) == nullptr,
          "a pattern longer than the buffer is refused");
    Check(AobScan::Scan(buf, size, head, nullptr, 0) == nullptr, "a zero-length pattern is refused");
    Check(AobScan::Scan(nullptr, size, head, nullptr, sizeof(head)) == nullptr, "a null buffer is refused");
    Check(AobScan::Scan(buf, size, nullptr, nullptr, 4) == nullptr, "a null pattern is refused");

    // 3) 通配符：mask 0x00 的字节任意
    const unsigned char wild[] = {0x8b, 0x00, 0x00, 0x08};
    const unsigned char mask[] = {0xFF, 0x00, 0x00, 0xFF};
    Check(AobScan::Scan(buf, size, wild, mask, sizeof(wild)) == buf + 8, "wildcard bytes match anything");
    const unsigned char wrongFirst[] = {0x8c, 0x00, 0x00, 0x08};
    Check(AobScan::Scan(buf, size, wrongFirst, mask, sizeof(wrongFirst)) == nullptr,
          "a non-wildcard byte that differs still fails");
    const unsigned char wrongLast[] = {0x8b, 0x00, 0x00, 0x09};
    Check(AobScan::Scan(buf, size, wrongLast, mask, sizeof(wrongLast)) == nullptr,
          "the last exact byte is still enforced");

    // 4) 唯一性：多个命中必须拒绝（宁可不用，也不猜）
    const unsigned char twice[] = {0xAA, 0xAA};
    const unsigned char twice3[] = {0xAA, 0xAA, 0xAA};
    Check(AobScan::Scan(twice3, sizeof(twice3), twice, nullptr, sizeof(twice)) == twice3,
          "scan returns the first of overlapping matches");
    Check(AobScan::ScanUnique(twice3, sizeof(twice3), twice, nullptr, sizeof(twice)) == nullptr,
          "an overlapping double match is refused by the unique scan");
    Check(AobScan::ScanUnique(buf, size, mid, nullptr, sizeof(mid)) == buf + 8,
          "a single match is accepted by the unique scan");
    Check(AobScan::ScanUnique(buf, size, absent, nullptr, sizeof(absent)) == nullptr,
          "no match means no result from the unique scan");

    // 5) 全通配符 = 到处都命中 ⇒ 必须拒绝（最容易误判的一类）
    const unsigned char any4[] = {0x00, 0x00, 0x00, 0x00};
    const unsigned char anyMask[] = {0x00, 0x00, 0x00, 0x00};
    Check(AobScan::ScanUnique(buf, size, any4, anyMask, sizeof(any4)) == nullptr,
          "an all-wildcard pattern is refused as non-unique");

    // 6) 唯一命中出现在最后一个可能位置时，第二次搜索的窗口不能越界
    const unsigned char singleTail[] = {0xBB, 0xCC};
    Check(AobScan::ScanUnique(buf, size, singleTail, nullptr, sizeof(singleTail)) == buf + size - 2,
          "a unique match at the end does not overrun the window");

    // 7) PE 定位：真实模块读不到就失败关闭（这里传 nullptr 与垃圾地址）
    Check(!AobScan::TextSection(nullptr).ok, "no module base means no .text range");
    unsigned char junk[64] = {};
    Check(!AobScan::TextSection(junk).ok, "garbage instead of a PE header is refused");
    Check(AobScan::FindInText(nullptr, head, nullptr, sizeof(head)) == nullptr,
          "find in text fails closed without a module");

    printf("RESULT aob-scan checks=%d failures=%d\n", checks, failures);
    return failures == 0 ? 0 : 1;
}
