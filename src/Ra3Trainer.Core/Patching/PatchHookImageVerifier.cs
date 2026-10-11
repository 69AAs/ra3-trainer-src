using System.Globalization;
using Ra3Trainer.Core.Manifest;

namespace Ra3Trainer.Core.Patching;

public sealed record PatchHookImageVerificationResult(
    string Address,
    string SectionTitle,
    int Rva,
    int RawOffset,
    string SectionName,
    byte[] ExpectedBytes,
    byte[] ImageBytes,
    bool Matches);

public static class PatchHookImageVerifier
{
    public static IReadOnlyList<PatchHookImageVerificationResult> Verify(PatchManifest manifest, PeImage image)
    {
        return manifest.Hooks
            .Where(hook => hook.TrampolineTarget is not null)
            .Select(hook =>
            {
                var expectedBytes = OriginalByteParser.Parse(hook.OriginalAssembly);
                var rva = ParseRva(hook.Address);
                var mapping = image.MapRva(rva, expectedBytes.Length);
                var imageBytes = image.ReadRva(rva, expectedBytes.Length);
                return new PatchHookImageVerificationResult(
                    hook.Address,
                    hook.SectionTitle,
                    rva,
                    mapping.RawOffset,
                    mapping.Section.Name,
                    expectedBytes,
                    imageBytes,
                    expectedBytes.Length > 0 && imageBytes.SequenceEqual(expectedBytes));
            })
            .ToArray();
    }

    internal static int ParseRva(string address)
    {
        var plusIndex = address.LastIndexOf('+');
        if (plusIndex < 0 || plusIndex == address.Length - 1)
        {
            throw new InvalidOperationException($"Unsupported hook address '{address}'.");
        }

        try
        {
            return int.Parse(address[(plusIndex + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // 调用方只按 InvalidOperationException 兜底（本方法上面那条也是这个类型）：
            // "ra3+ZZZ"、超过 8 位十六进制的后缀原来会抛 Format/Overflow 逃出去，变成一次未处理异常。
            throw new InvalidOperationException($"Unsupported hook address '{address}'.", ex);
        }
    }
}
