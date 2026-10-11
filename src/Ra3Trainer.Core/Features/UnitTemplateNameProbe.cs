using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

namespace Ra3Trainer.Core.Features;

/// <summary>
/// 从**选中的那个单位对象**的模板里，读它真正的内部名（例如 <c>SovietNewTower</c>）——**纯只读**。
///
/// <para><b>用户 2026-10-11</b>：「扫出来的名字是新单位 0x12345678 这种，能不能用模板里的内部名去查中文名？」</para>
///
/// <para>取证依据（<c>tools/limit_probe.py</c> 实测）：模板对象附近**没有固定偏移**能拿到名字，
/// 但模板前 0x300 字节里有一个**指向可打印字符串的指针**（塔 = <c>SovietNewTower</c>、收割机甲 =
/// <c>SovietHeavyWalkerVehicle</c>，两次都命中）。所以这里照它做：</para>
/// <list type="number">
/// <item>在 [模板, 模板+<see cref="ScanSpan"/>) 里按 4 字节步进找"像指针"的 dword（范围内 + 4 字节对齐）；</item>
/// <item>读它指向的字符串（≤ <see cref="MaxNameLength"/> 字节，要求 NUL 结尾 + 全是可打印 ASCII）；</item>
/// <item>优先选**能在内置内部名表里对上**的那条（可信度最高）；否则选第一条"像内部名"的候选。</item>
/// </list>
///
/// <para><b>安全</b>：只调 <see cref="IProcessMemory.ReadBytes"/>，**不写一个字节**，所以它按构造就不可能崩游戏
/// （与人用户的红线一致：调引擎内部函数才会崩，读数不会）。表里查不到就返回 null，调用方落回「未命名」。
/// 读取次数有上界（<see cref="ScanSpan"/> ÷ 4），不会卡。</para>
/// </summary>
public static class UnitTemplateNameProbe
{
    /// <summary>在模板对象开头这么多字节里找名字指针（与只读探针一致）。</summary>
    public const int ScanSpan = 0x300;

    /// <summary>名字最长读这么多字节（内部名都很短，长了就是读歪）。</summary>
    public const int MaxNameLength = 64;

    /// <summary>
    /// 尽力读出模板的内部名；读不到就 null（**绝不抛**）。
    /// </summary>
    /// <param name="memory">目标进程内存（只读用）。</param>
    /// <param name="template">模板对象地址（单位对象 + 4）。</param>
    /// <param name="knownInternalNames">可信名字集（内置内部名表）：命中即认定为内部名。</param>
    public static string? TryRead(IProcessMemory memory, nint template,
        IReadOnlyCollection<string> knownInternalNames)
    {
        if (memory is null || template == 0) return null;

        var known = new HashSet<string>(knownInternalNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        string? firstLookalike = null;

        for (var offset = 0; offset < ScanSpan; offset += 4)
        {
            nint candidate;
            try
            {
                var raw = memory.ReadBytes(template + offset, 4);
                if (raw.Length != 4) break;
                var value = BitConverter.ToUInt32(raw);
                if (value < EmpiricalSafetyBounds.MinimumPointer || value > EmpiricalSafetyBounds.MaximumPointer) continue;
                if ((value & 3) != 0) continue;
                candidate = unchecked((nint)(nuint)value);
            }
            catch
            {
                return firstLookalike;   // 模板尾部跨到未提交页：已找到什么就用什么
            }

            var text = TryReadAscii(memory, candidate);
            if (text is null) continue;
            if (known.Contains(text)) return text;                  // 对上了内置表 ⇒ 最可信，直接定
            firstLookalike ??= text;
        }

        return firstLookalike is not null && LooksLikeInternalName(firstLookalike) ? firstLookalike : null;
    }

    /// <summary>读一个 NUL 结尾的可打印 ASCII 字符串；不合格返回 null（不抛）。</summary>
    private static string? TryReadAscii(IProcessMemory memory, nint address)
    {
        byte[] bytes;
        try
        {
            bytes = memory.ReadBytes(address, MaxNameLength);
        }
        catch
        {
            return null;
        }

        if (bytes.Length == 0) return null;
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 3) return null;                                   // 空串/一两个字符：不是内部名
        for (var i = 0; i < end; i++)
        {
            var b = bytes[i];
            var printable = b is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'_' or (byte)'-' or (byte)'.';
            if (!printable) return null;
        }

        return System.Text.Encoding.ASCII.GetString(bytes, 0, end);
    }

    /// <summary>像不像一个内部名：以字母开头、至少两个大写字母（`SovietNewTower` 这种驼峰）。</summary>
    public static bool LooksLikeInternalName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3 || text.Length > MaxNameLength) return false;
        if (!char.IsAsciiLetter(text[0])) return false;
        var upper = text.Count(char.IsAsciiLetterUpper);
        return upper >= 2;
    }
}
