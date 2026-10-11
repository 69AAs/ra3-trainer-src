namespace Ra3Trainer.Core.Features;

/// <summary>经验观测边界，不是游戏引擎不变量；超出时保守拒绝。</summary>
public static class EmpiricalSafetyBounds
{
    // docs/round19-samples/*.jsonl：三份、31808 行；逐行复算见
    // docs/round19-audit/statistics.json（整数取 Bits，非零分位数单独统计）。
    // head health 9790 / tail 9787；当前血量含死亡0与修改值999999，
    // head 基础血量100..50000；仍保守拒绝死亡，保留执行上限100000及显式修改值。
    // 坐标副本31127行非零、681行全零；X=13.864563..9908.259766，
    // Y=101..9839.143555，Z=200..599.294434。独立head位置X最大9868.839844。
    // 将旧8000经验上界按观测重定为向上取整到千位的10000，不证明地图合法性；
    // 保持下界、有限值、三个非零哨兵、本次转换及完整根链检查。
    // first speed：head8996/tail8993，10..250；保留0/10/500修改协议。
    // 数量selection最大1806、增援参数8..50（不是实际创建数量证明）；等级参数仅3。
    /// <summary>
    /// 「数值理智线」（**不是游戏经验边界**）：只用来挡 NaN / 0 / 天文数字那一类垃圾读数。
    ///
    /// <para>为什么不再给"**按对象变化**的值"（血量、单位坐标、移动速度、相机缩放）定经验上界：
    /// 那些值随 mod、地图和建筑种类变，经验上界迟早把真值判成非法 —— 已经踩过两次，
    /// 每次现场表现都和"功能坏了"一模一样（他只会说"XX 不能用了"）：</para>
    /// <list type="bullet">
    /// <item>坐标上界定在 8000，他的地图能到 9908；</item>
    /// <item>血量上界定在 100000，他选的建筑满血 125000（2026-10-08 00:02:42 真机原文：
    /// 「生命值超出经验范围或对象已死亡（读到 125000，边界 [1, 100000]，地址 0x57E3E0C）」）。</item>
    /// </list>
    /// <para>1e9 远超任何游戏内数值，同时仍然挡得住"把指针当 float 读"的典型垃圾（1e38 那一类）。
    /// 真正防坏事的是**结构性**那几条：指针可读/对齐/落在提交页内、链的拓扑、字段身份校验、写后回读。</para>
    /// </summary>
    public const float NumericSanityCeiling = 1e9f;

    public const float MinimumHealth = 1;
    // 保留这两个名字是给"要写进去的定值"用的（HP MIN 期望 1、HP MAX 期望 999999），不再当读入值的上界：
    // 读入血量走 NumericSanityCeiling（见上）。
    public const float MaximumHealth = 1000000;
    public const float ModifiedHealth = 999999; // bootstrap 实际 0x497423F0，非注释中的 9999999。
    public const float MinimumCoordinate = -1;
    public const float MaximumCoordinate = 10000;
    public const float MinimumSpeed = 0;
    public const float MaximumSpeed = 500; // 包含 bootstrap 的冻结 0、慢速 10、加速 500。
    public const float FrozenSpeed = 0;
    public const float SlowSpeed = 10;
    public const float FastSpeed = 500;
    public const float SecondaryMovementActive = 1; // bootstrap 的 +18 比较值。
    public const uint MaximumRank = 3; // 既有 ReinforcementSettings 的等级协议。
    public const uint ObservedMaximumSelection = 1806;
    public const uint MaximumActionTargets = 1; // 每次操作目标数，不再用作 selection.count 上限。
    // Round28: 36,161 real probe rows / 36 parameter types; observed N = 8/10/20/50.
    // docs/round28-evidence/quantity-table.md: no type has a proved completed-N interval.
    // Missing batch-linked epochs, actual-created counts and frame timing; no engine cap evidence.
    // 50 remains a legacy parameter ceiling, NOT a measured capacity. Keep in-segment clamp.
    public const int MaximumReinforcementCount = 50;
    public const uint MinimumPointer = 0x10000;
    public const uint MaximumPointer = 0xFFFEFFFF; // LAA x86；必须另查提交状态/保护属性。
    // docs/round15-audit/camera-mouse-evidence.txt 单份 dump：40/800。
    public const float MinimumZoom = 40;
    public const float MaximumZoom = 800;
}
