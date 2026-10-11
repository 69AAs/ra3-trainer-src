using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

/// <summary>
/// 远程状态字段表就是修改器与原生 DLL 之间的协议：常量改错/撞车在离线时没有任何东西会红，
/// 只有真机跑起来才发现（或更糟：静默写错槽）。这里把 22 个字段全部钉死。
/// </summary>
public sealed class RemoteStateLayoutTests
{
    [Fact]
    public void LayoutConstantsMatchTheBootstrapAllocation()
    {
        Assert.Equal("iEnable+20", RemoteStateLayout.ActionDispatch);
        Assert.Equal("iEnable+24", RemoteStateLayout.ReinforcementUnitId);
        Assert.Equal("iEnable+28", RemoteStateLayout.ReinforcementCount);
        Assert.Equal("iEnable+2C", RemoteStateLayout.ReinforcementRank);
        Assert.Equal("iEnable+30", RemoteStateLayout.MoneyAmount);
        Assert.Equal("iEnable+34", RemoteStateLayout.PowerValue);
        Assert.Equal("iEnable+38", RemoteStateLayout.ScPointValue);

        // 0x100 字节分配里 +3C..+FF 原本是空的，这四个是后加的协议字段：
        // DLL 侧对 +40 做 DWORD XCHG、对 +44/+48 做 epoch 比较、+4C 读生成失败位。
        Assert.Equal("iEnable+40", RemoteStateLayout.DispatchLock);
        Assert.Equal("iEnable+44", RemoteStateLayout.ConversionEpoch);
        Assert.Equal("iEnable+48", RemoteStateLayout.DispatchEpoch);
        Assert.Equal("iEnable+4C", RemoteStateLayout.CreationFailure);

        // 坐标转换派发用的动作码（MustCode2+D00），派发与守卫两边都按它认。
        Assert.Equal(0x0E, RemoteStateLayout.CoordinateConversionDispatch);

        Assert.Equal("MustCode+1080", RemoteStateLayout.WorldCoordinates);
        Assert.Equal("ra3_1.12.game+8E9838", RemoteStateLayout.SelectedUnitCode);
    }

    [Fact]
    public void IEnableFieldsAreDistinctAlignedAndInsideTheAllocation()
    {
        var fields = new (string Name, string Expression)[]
        {
            (nameof(RemoteStateLayout.ActionDispatch), RemoteStateLayout.ActionDispatch),
            (nameof(RemoteStateLayout.ReinforcementUnitId), RemoteStateLayout.ReinforcementUnitId),
            (nameof(RemoteStateLayout.ReinforcementCount), RemoteStateLayout.ReinforcementCount),
            (nameof(RemoteStateLayout.ReinforcementRank), RemoteStateLayout.ReinforcementRank),
            (nameof(RemoteStateLayout.MoneyAmount), RemoteStateLayout.MoneyAmount),
            (nameof(RemoteStateLayout.PowerValue), RemoteStateLayout.PowerValue),
            (nameof(RemoteStateLayout.ScPointValue), RemoteStateLayout.ScPointValue),
            (nameof(RemoteStateLayout.DispatchLock), RemoteStateLayout.DispatchLock),
            (nameof(RemoteStateLayout.ConversionEpoch), RemoteStateLayout.ConversionEpoch),
            (nameof(RemoteStateLayout.DispatchEpoch), RemoteStateLayout.DispatchEpoch),
            (nameof(RemoteStateLayout.CreationFailure), RemoteStateLayout.CreationFailure)
        };

        var offsets = new List<(string Name, int Offset)>();
        foreach (var (name, expression) in fields)
        {
            Assert.StartsWith("iEnable+", expression, StringComparison.Ordinal);
            var offset = Convert.ToInt32(expression["iEnable+".Length..], 16);
            Assert.True(offset % 4 == 0, $"{name} 偏移 {expression} 未按 4 字节对齐（DLL 侧按 DWORD 读写）。");
            Assert.InRange(offset, 0, 0xFF - 3);   // 每个字段占 4 字节，且都在 0x100 的 iEnable 分配里
            offsets.Add((name, offset));
        }

        // 撞车检查：任意两个 4 字节字段不得重叠（原来是靠人眼看十六进制）。
        foreach (var (name, offset) in offsets)
        {
            foreach (var (otherName, otherOffset) in offsets)
            {
                if (name == otherName)
                {
                    continue;
                }

                Assert.False(offset < otherOffset + 4 && otherOffset < offset + 4,
                    $"{name}(+{offset:X}) 与 {otherName}(+{otherOffset:X}) 的 4 字节区间重叠。");
            }
        }
    }
}
