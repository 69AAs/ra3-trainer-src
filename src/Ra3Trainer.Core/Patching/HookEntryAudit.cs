using Iced.Intel;

namespace Ra3Trainer.Core.Patching;

public sealed record InternalHookEntry(ulong Source, ulong Target, string Instruction);

/// <summary>Audits a decoded x86 code region. The caller supplies a real instruction boundary
/// and all executable regions that may branch to the patch. Indirect targets require separate analysis.</summary>
public static class HookEntryAudit
{
    public static IReadOnlyList<InternalHookEntry> Find(byte[] code, uint imageAddress, uint site, int patchLength)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (patchLength <= 0 || (ulong)site + (uint)patchLength > 0x1_0000_0000UL)
            throw new ArgumentOutOfRangeException(nameof(patchLength));
        if ((ulong)imageAddress + (uint)code.Length > 0x1_0000_0000UL)
            throw new ArgumentOutOfRangeException(nameof(imageAddress));
        // 被审的补丁区间必须整段落在喂进来的 code 里：否则"没找到内部入口"只是没解码过那段，
        // Verify 会给出假的通过（fail-closed 的契约就破了）。
        if ((ulong)site < imageAddress || (ulong)site + (uint)patchLength > (ulong)imageAddress + (uint)code.Length)
            throw new ArgumentOutOfRangeException(nameof(site),
                $"补丁区间 0x{site:X8}+{patchLength} 不在被审代码区 [0x{imageAddress:X8}, 0x{(ulong)imageAddress + (uint)code.Length:X8}) 内。");
        var reader = new ByteArrayCodeReader(code);
        var decoder = Decoder.Create(32, reader);
        decoder.IP = imageAddress;
        var entries = new List<InternalHookEntry>();
        var formatter = new NasmFormatter();
        while (reader.CanReadByte)
        {
            decoder.Decode(out var instruction);
            if (instruction.Op0Kind is not (OpKind.NearBranch16 or OpKind.NearBranch32)) continue;
            var target = instruction.NearBranchTarget;
            if (target <= site || target >= (ulong)site + (uint)patchLength) continue;
            var text = new StringOutput();
            formatter.Format(instruction, text);
            entries.Add(new(instruction.IP, target, text.ToString()));
        }
        return entries;
    }

    /// <summary>
    /// 范围内存在"直接近跳转"入口时抛异常。<b>能力边界</b>：只解码直接近跳转（NearBranch16/32），
    /// 间接（jmp/call r/m）与远跳转的运行时目标静态不可判定，见类注释——通过 ≠ 该区域绝对无入口。
    /// </summary>
    public static void Verify(byte[] code, uint imageAddress, uint site, int patchLength)
    {
        var entries = Find(code, imageAddress, site, patchLength);
        if (entries.Count != 0)
            throw new InvalidOperationException($"补丁 0x{site:X8}+{patchLength} 存在内部入口：" +
                string.Join("；", entries.Select(entry => $"0x{entry.Source:X8} → 0x{entry.Target:X8} ({entry.Instruction})")));
    }
}
