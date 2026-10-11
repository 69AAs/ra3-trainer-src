using Ra3Trainer.Core.Manifest;
using Ra3Trainer.Core.Memory;
using Ra3Trainer.Core.Runtime;

using System.IO;
using System.Linq;

namespace Ra3Trainer.Core.Features;

/// <summary>主机只读预检；页面可读不等于类型、生命周期或未来执行安全。</summary>
internal sealed class SelectedObjectGuard(IProbeMemory memory, AddressResolver resolver)
{
    private readonly List<(nint Address, uint Value)> _pointers = [];
    private readonly List<(nint Address, uint Value)> _expected = [];

    public void Validate(TrainerFeature feature, ReinforcementSettings? settings = null, bool validateCoordinates = false)
    {
        if (!FeatureSafetyPolicy.RequiresWorldPositionOptIn(feature) && !FeatureSafetyPolicy.IsCoordinateAction(feature)) return;
        try
        {
            _pointers.Clear();
            _expected.Clear();
            var name = feature.RawName.ToUpperInvariant();
            if (FeatureSafetyPolicy.IsCoordinateAction(feature))
            {
                var expected = name switch { "GET ME BASE" => ("MustCode2+A00", "0x0B"),
                    "WE NEED BACK" => ("MustCode2+B00", "0x0C"),
                    "SELECT UNIT COPY FOR ME" => ("MustCode2+C00", "0x0D"), _ => ("", "") };
                if (expected.Item1.Length == 0 || !string.Equals(feature.DispatchTarget, expected.Item1, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(feature.ValueHint, expected.Item2, StringComparison.OrdinalIgnoreCase))
                    Reject("坐标动作协议不匹配，禁止绕过两阶段校验");
                var mouse = Pointer(resolver.Resolve("ra3_1.12.game+8DAEFC"));
                Read(mouse + 0x3C); Read(mouse + 0x40); Read(mouse + 0x44);
                var player = Pointer(resolver.Resolve("ID"));
                Read(player + 0x10); // CreateUnit passes this scalar and player to game+205240.
                Pointer(resolver.Resolve("ra3_1.12.game+8E6C58")); // GetUnitData2 receiver.
                if (name == "WE NEED BACK") ValidateType((settings ?? ReinforcementSettings.Default).UnitId);
                if (validateCoordinates)
                {
                    var world = new float[3];
                    var index = 0;
                    foreach (var offset in new[] { 0, 4, 8 })
                    {
                        var coordinate = Float(resolver.Resolve(RemoteStateLayout.WorldCoordinates) + offset,
                            EmpiricalSafetyBounds.MinimumCoordinate, EmpiricalSafetyBounds.MaximumCoordinate, "世界坐标");
                        if (coordinate == 0) Reject("世界坐标仍为零哨兵，本次转换不新鲜或未完整写入，已拒绝生成");
                        world[index++] = coordinate;
                    }

                    // 引擎内部发兵用：这份坐标是**游戏自己**从鼠标换算出来的（MustCode+1080），
                    // 导出给 DLL 读，DLL 再用引擎动作 CREATE_OBJECT 在同一个位置生成单位。
                    // 纯附加：写文件失败绝不影响原有流程。
                    try
                    {
                        // 单位类型码：修改器本来就在读"当前选中单位"的类型码（SelectedUnitCode）。
                        var unitCode = resolver.Resolve(RemoteStateLayout.SelectedUnitCode);
                        var selected = unitCode == 0 ? 0u : (uint)Read(unitCode);
                        // 写入改成"临时文件 + 原子替换"：DLL 会直接读这个固定路径，
                        // 原来的 WriteAllText 半途失败（磁盘满/ACL）会让它读到截断的数字串，
                        // 进而按错的坐标发兵。文件名保持约定不变（原生侧写死了这个路径）。
                        var target = Path.Combine(Path.GetTempPath(), "ra3-summon-target.txt");
                        // 第 5 个数 = iEnable 基址：DLL 用它去读游戏写的"生成失败"槽（iEnable+4C），
                        // 这样引擎那边为什么没生成，游戏自己会告诉我们，不用靠猜。
                        // 直接解析"生成失败"槽本身的地址（iEnable+4C）——修改器自己读写它时就是这么解析的，
                        // 比去拼 iEnable 基址可靠。DLL 会只读这个地址，用它知道引擎为什么没生成。
                        var enableBase = resolver.Resolve(RemoteStateLayout.CreationFailure);
                        // 第 6 个数 = 当前生效增援预设的 UnitId：就是"面板上选定的那个单位"。
                        // DLL 用它当"要生成的兵"，这样引擎路发的兵与面板选择一致，而不是用一个来源不明的码。
                        var presetUnitId = settings?.UnitId ?? 0;
                        var staging = target + ".tmp" + Environment.ProcessId.ToString("X");
                        File.WriteAllText(staging, string.Join(" ", world.Select(v =>
                                v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
                            + " " + selected.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " " + enableBase.ToString(System.Globalization.CultureInfo.InvariantCulture)
                            + " " + presetUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        File.Move(staging, target, overwrite: true);
                    }
                    catch
                    {
                        // 导出失败只是少一条路，不能影响本次动作。
                    }
                }
                if (name != "SELECT UNIT COPY FOR ME") { Recheck(); return; }
            }
            if (name == "FREE BUILD")
                Reject("建造目标与地形合法性缺少可只读校验的对象根链：已定位的网格判定依赖游戏上下文且会写入网格状态，现有随地建造只绕过条件分支；已拒绝启用随地建造，未写入补丁");
            if (name == "ZOOM")
            {
                var camera = Pointer(resolver.Resolve("ra3_1.12.game+8DB7B4"));
                Pointer(camera); // dump 中相机首字段为 vtable。
                // 相机上下限的绝对区间随 mod/分辨率变 ⇒ 只留"结构关系"（下限 ≤ 上限、当前缩放落在两者之间）。
                var minimum = FloatSanity(camera + 0x2700, "相机下限");
                var maximum = FloatSanity(camera + 0x2704, "相机上限");
                if (minimum > maximum) Reject("相机上下限顺序无效");
                Float(camera + 0x44, minimum, maximum, "相机当前缩放");
                Recheck();
                return;
            }
            if (!FeatureSafetyPolicy.RequiresSelectedObjectValidation(feature) && name != "SELECT UNIT COPY FOR ME")
                Reject("运行时目标缺少可只读校验的对象根链");
            var root = Pointer(resolver.Resolve("ra3_1.12.game+8DB73C"));
            var count = Read(root + 0x5C);
            if (count == 0) Reject("当前没有选中单位");
            _pointers.Add((root + 0x5C, count));
            // Only the head payload is relevant. Its first word is next: never read it,
            // including as a generic pointer landing probe, and never inspect root+54 (tail).
            var node = Pointer(root + 0x50, landingOffset: 8);
            var wrapper = Pointer(node + 8);
            var unit = Pointer(wrapper + 0x138);
            var template = Pointer(unit + 4);
            // game+3E4278 reads template+8 as the key for the same type map used
            // by game+3E4230; see round19 type-resolver/head-template evidence.
            var type = Read(template + 8);
            // ★ 「类型必须在内置单位目录里」只对**要拿类型码去生成/匹配**的功能有意义（增援、复制…）。
            //   「改归属」(SELECT UNIT CHANGE ID) 不需要它：引擎那段动作只比它自己的 6 型黑名单，不查我们的增援目录，
            //   而建筑与别的 mod 单位本来就不在那 495 条清单里 —— 2026-10-08 00:02:59 实测就是这么被**我们自己**挡下的
            //   （日志原文：「…无法校验类型，已拒绝生成（type=0x7CA957C7）」，引擎侧根本没参与）。
            //   这里只要求类型**读得出来且非 0**；归属写入本身另有：链上指针齐备 + 归属字段身份校验 + 转让黑名单 + 写后回读确认。
            if (name == "SELECT UNIT CHANGE ID")
            {
                if (type == 0) Reject("单位类型读出来是 0，无法确认对象，已拒绝");
            }
            else
            {
                ValidateType(type);
            }
            _pointers.Add((template + 8, type));
            var health = Pointer(unit + 0x33C);
            foreach (var offset in new[] { 4, 0xC, 0x10 }) Health(health + offset);
            // 单位坐标同样是"按地图变"的值 ⇒ 只做数值理智，不再套地图经验窗口（他地图跑到 9908 时就是这么被误拒的）。
            foreach (var offset in new[] { 0x38, 0x3C, 0x40 }) FloatSanity(unit + offset, "单位坐标");
            if (name is "SELECT UNIT SUPER SPEED" or "SELECT UNIT SLOW SPEED" or "SELECT UNIT FREEZE" or "RESTORE SELECT UNIT SPEED")
            {
                var table = Pointer(Pointer(unit + 0x374) + 0x200);
                var first = Pointer(table);
                Speed(first, name);
                var second = Pointer(table + 4);
                var mode = Float(second + 0x18, 0, EmpiricalSafetyBounds.SecondaryMovementActive, "移动组件模式");
                // 原脚本仅 mode==1 时访问第二组件的速度。原始样本其它模式的位值不能当速度。
                if (mode == EmpiricalSafetyBounds.SecondaryMovementActive) Speed(second, name);
            }
            if (name == "SELECT UNIT LEVEL UP")
            {
                Pointer(unit + 0x3CC);
                var rank = Read(resolver.Resolve("iEnable+2C"));
                if (rank > EmpiricalSafetyBounds.MaximumRank) Reject("单位等级参数超出 0 至 3 范围");
            }
            if (name == "SELECT UNIT COPY FOR ME") Pointer(unit + 0x418);
            if (name == "SELECT UNIT CHANGE ID")
            {
                var owner = Pointer(unit + 0x418);
                var player = Pointer(resolver.Resolve("ID"));
                if (new uint[] { 0x95D6E965, 0xFD87E82A, 0x856C9DD6, 0xD20552E1, 0x1AFC9A6E, 0xD6D22475 }.Contains(type))
                    Reject($"当前单位类型不支持转让（type=0x{type:X8}）");
                _expected.Add((unit + 0x418, unchecked((uint)(nuint)player)));
            }
            if (name == "SELECT UNIT HP MAX")
            {
                Expect(health + 4, EmpiricalSafetyBounds.ModifiedHealth);
                Expect(health + 0xC, EmpiricalSafetyBounds.ModifiedHealth);
            }
            if (name == "SELECT UNIT HP MIN") Expect(health + 4, EmpiricalSafetyBounds.MinimumHealth);
            if (name == "RESTORE SELECT UNIT NORMAL HP")
            {
                _expected.Add((health + 4, Read(health + 0x10)));
                _expected.Add((health + 0xC, Read(health + 0x10)));
            }
            if (name is "SELECT UNIT AMMO MAX" or "SELECT UNIT AMMO MAX 2")
                Reject("无限弹药有过实机卡死记录；缺少必要前置条件，即使开启高风险功能也不会执行");
            if (name == "RESTORE SELECT ORE MINE")
                Reject("矿脉与选中对象的对应关系还没确认，即使开启高风险功能也不会执行");
            Recheck();
            Health(health + 4);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{feature.DisplayName}：对象根链校验未通过，已拒绝执行，未写入功能标志。{ex.Message}。", ex);
        }
    }

    public void Confirm()
    {
        if (_expected.Count == 0) return;
        try
        {
            // 转让会有意改变 owner 字段；其它身份必须仍相同。
            foreach (var (address, value) in _pointers)
                if (!_expected.Any(e => e.Address == address) && Read(address) != value)
                    Reject("写后对象身份变化，无法确认结果；未尝试回滚动作");
            foreach (var (address, value) in _expected)
                if (Read(address) != value) Reject("动作已被消费，但目标字段写后读回不符；未尝试回滚动作");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw new InvalidOperationException("写后确认失败：" + ex.Message, ex); }
    }

    private static HashSet<uint>? _userKnownTypes;
    private static DateTime _userKnownTypesReadAt;
    private static readonly object UserKnownTypesGate = new();

    /// <summary>
    /// 他自己扫出来的模组单位目录（exe 旁的 `units-*.txt`）里的类型码。
    /// ★ 2026-10-11 修 ★ 这些码以前**完全不参与校验** —— `ValidateType` 只认内置目录，
    /// 于是所有 mod 单位（REMIX 的 0xB31A4632 等）一律被拒，表现是"按 I / 点面板复制单位一点反应都没有"，
    /// 而日志只会写一句"类型不在已知单位目录中"。真机实证：REMIX 的 SovietNewTower 连拒 33 次。
    /// 这些文件是用户在游戏里自己扫出来的，可信度等同内置目录；读不到就保持原来的严格行为。
    /// </summary>
    private static HashSet<uint> UserKnownTypes()
    {
        lock (UserKnownTypesGate)
        {
            if (_userKnownTypes is not null && DateTime.UtcNow - _userKnownTypesReadAt < TimeSpan.FromSeconds(5))
            {
                return _userKnownTypes;
            }

            var set = new HashSet<uint>();
            try
            {
                foreach (var path in TrainerPaths.SearchDirectories(AppContext.BaseDirectory)
                    .Where(Directory.Exists)
                    .SelectMany(dir => Directory.EnumerateFiles(dir, "units-*.txt")))
                {
                    foreach (var entry in ReinforcementUnitCatalog.LoadUserCatalog(path))
                    {
                        set.Add(entry.Code);
                    }
                }
            }
            catch (Exception)
            {
                // 目录读不到：当作没有额外已知类型，保持原有严格行为。
            }

            _userKnownTypes = set;
            _userKnownTypesReadAt = DateTime.UtcNow;
            return set;
        }
    }

    private static void ValidateType(uint type)
    {
        if (!ReinforcementUnitCatalog.LoadBuiltIn().Any(entry => entry.Code == type) && !UserKnownTypes().Contains(type))
            // 把读到的类型码写进拒绝文案：没有它，日志只能告诉我们"被拒了"，
            // 有它就能一眼分清"目录外的新 mod 单位/建筑"还是"读歪了"（ReadOnlyProbe 里就是这么踩出来的经验）。
            Reject($"单位类型不在已知单位目录中，无法校验类型，已拒绝生成（type=0x{type:X8}）");
    }

    private void Speed(nint component, string name)
    {
        // 速度：下限 0 是语义（负速度无意义），上界只到理智线 —— 不用 500 那个经验值（它来自 bootstrap 写过的定值）。
        var current = Float(component + 8, 0, EmpiricalSafetyBounds.NumericSanityCeiling, "移动速度");
        var saved = Float(component + 0x40, 0, EmpiricalSafetyBounds.NumericSanityCeiling, "移动备份速度");
        var value = name switch { "SELECT UNIT SUPER SPEED" => EmpiricalSafetyBounds.FastSpeed, "SELECT UNIT SLOW SPEED" => EmpiricalSafetyBounds.SlowSpeed,
            "SELECT UNIT FREEZE" => EmpiricalSafetyBounds.FrozenSpeed, _ => current is EmpiricalSafetyBounds.FrozenSpeed or EmpiricalSafetyBounds.SlowSpeed or EmpiricalSafetyBounds.FastSpeed ? saved : current };
        Expect(component + 8, value);
    }
    private void Expect(nint address, float value) => _expected.Add((address, BitConverter.SingleToUInt32Bits(value)));
    private void Recheck()
    {
        foreach (var (address, value) in _pointers)
            if (Read(address) != value) Reject("选中对象已变化或被摧毁");
    }
    private nint Pointer(nint address, int landingOffset = 0)
    {
        var value = Read(address);
        if (value < EmpiricalSafetyBounds.MinimumPointer || value > EmpiricalSafetyBounds.MaximumPointer || (value & 3) != 0)
            Reject($"对象指针为空、越界或未对齐（读到 0x{value:X8}，来自 0x{(ulong)address:X}）");
        var pointer = unchecked((nint)(nuint)value);
        Read(pointer + landingOffset);
        _pointers.Add((address, value));
        return pointer;
    }
    private uint Read(nint address)
    {
        if (!ReadOnlyProbe.Readable(memory.Query(address), address, 4))
            Reject("选中对象链字段未提交、不可读或跨越内存区域");
        var bytes = memory.ReadBytes(address, 4);
        if (bytes.Length != 4) Reject("对象字段读取不完整");
        return BitConverter.ToUInt32(bytes);
    }
    /// <summary>
    /// 血量：只要"有限正数 + 不超数值理智线"。**不再设游戏经验上界** —— 那条上界两次把真值判成非法
    /// （坐标 8000→他地图 9908；血量 100000→他选的建筑满血 125000），"按每个建筑去测上界"也不可行。
    /// 拒绝时把**读到的值**打出来：下一个人一眼能看出是"读歪了"还是"我们的线不对"。
    /// </summary>
    private void Health(nint address)
    {
        var value = BitConverter.UInt32BitsToSingle(Read(address));
        if (value == EmpiricalSafetyBounds.ModifiedHealth) return;   // 我们自己的 HP MAX 写进去的定值
        if (!float.IsFinite(value) || value <= 0)
            Reject($"生命值不是有限正数（读到 {value:0.####}，地址 0x{(uint)(nuint)address:X8}）——对象可能已死亡或链读歪");
        if (value >= EmpiricalSafetyBounds.NumericSanityCeiling)
            Reject($"生命值超出数值理智线（读到 {value:0.####}，理智线 {EmpiricalSafetyBounds.NumericSanityCeiling:0}，地址 0x{(uint)(nuint)address:X8}）——只挡垃圾读数，不是游戏经验上界");
    }
    /// <summary>数值理智：只要有限且 |值| 不超理智线。用在"按 mod/地图变化"的字段上（见 NumericSanityCeiling 的说明）。</summary>
    private float FloatSanity(nint address, string field)
    {
        var value = BitConverter.UInt32BitsToSingle(Read(address));
        if (!float.IsFinite(value) || Math.Abs(value) >= EmpiricalSafetyBounds.NumericSanityCeiling)
            Reject($"{field}不是有限值或超出数值理智线（读到 {value:0.####}，理智线 ±{EmpiricalSafetyBounds.NumericSanityCeiling:0}）");
        return value;
    }
    private float Float(nint address, float minimum, float maximum, string field)
    {
        var value = BitConverter.UInt32BitsToSingle(Read(address));
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            Reject($"{field}不满足有限值范围 [{minimum}, {maximum}]（读到 {value:0.####}）");
        return value;
    }
    private static void Reject(string message) => throw new InvalidOperationException(message);
}
