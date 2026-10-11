namespace Ra3Trainer.Core.Features;

/// <summary>
/// 把守卫层的**技术拒绝文案**翻译成用户看得懂的一句话。2026-10-11 文案评审：
/// 守卫层 20 多条 Reject(...) 是写给开发者的（"零哨兵""对象根链""数值理智线""两阶段校验""链读歪"…），
/// 但它们会经 ex.Message 直接进状态行与游戏内面板 —— 用户看到的是一串黑话，也不知道下一步做什么。
/// 口径：**技术原文一个字不改地进日志**（排查要用），只有给人看的那一层走这里翻译。
/// 认不出来的一律原样返回，绝不吞信息。
/// </summary>
public static class FeatureErrorText
{
    public static string ToUserText(string? technical)
    {
        if (string.IsNullOrWhiteSpace(technical)) return "这次没成功。";

        return technical switch
        {
            var t when t.Contains("当前没有选中单位", StringComparison.Ordinal) => "先在游戏里选中一个单位，再按一次。",
            var t when t.Contains("不在已知单位目录", StringComparison.Ordinal) => "这个单位还没被识别：到「诊断」页点「扫描单位类型」，扫完再试。",
            var t when t.Contains("不支持转让", StringComparison.Ordinal) => "这个单位不能改成你的归属，换一个试试。",
            var t when t.Contains("对象根链", StringComparison.Ordinal) => "没能读稳这个单位（可能刚被摧毁或还在变化），重新选一次再试。",
            var t when t.Contains("选中对象链字段", StringComparison.Ordinal) => "没能读稳这个单位（可能刚被摧毁或还在变化），重新选一次再试。",
            var t when t.Contains("指针为空、越界", StringComparison.Ordinal) => "没能读稳这个单位（可能刚被摧毁或还在变化），重新选一次再试。",
            var t when t.Contains("选中对象已变化或被摧毁", StringComparison.Ordinal) => "选中的那个已经变了或被摧毁，重新选一个。",
            var t when t.Contains("生命值", StringComparison.Ordinal) => "这个单位的血量不是正常数值（可能已经死了），重新选一个。",
            var t when t.Contains("世界坐标", StringComparison.Ordinal) => "没读到鼠标当前位置：把鼠标放到目标地点，停一下再试。",
            var t when t.Contains("两阶段校验", StringComparison.Ordinal) => "这次的指令和目标对不上，已拦下（再点一次通常就好）。",
            var t when t.Contains("相机", StringComparison.Ordinal) => "缩放数据不正常：先转一下视角、再试。",
            var t when t.Contains("弹药", StringComparison.Ordinal) => "无限弹药曾把游戏卡死过，所以这条关掉了（需要时另说）。",
            var t when t.Contains("矿脉", StringComparison.Ordinal) => "先选中矿脉，再用这条。",
            var t when t.Contains("写后", StringComparison.Ordinal) => "写入之后没确认成功，可能没生效 —— 看一眼游戏里的数值，不行就重来。",
            var t when t.Contains("等级参数", StringComparison.Ordinal) => "等级只能是 0~3。",
            _ => technical,
        };
    }
}
