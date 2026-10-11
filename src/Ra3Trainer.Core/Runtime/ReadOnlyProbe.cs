using System.ComponentModel;
using Ra3Trainer.Core.Memory;

namespace Ra3Trainer.Core.Runtime;

public sealed record ProbeField(double? Value, string Status, uint? Bits = null,
    ulong? RegionBase = null, ulong? RegionSize = null, uint? Protection = null);

public sealed record ProbeSample(DateTimeOffset Timestamp, string Identity, bool HooksInstalled,
    bool SelectionStable, IReadOnlyDictionary<string, ProbeField> Fields,
    string Qualification = "只读观测；可读与有限不等于对象合法。坐标副本可能陈旧；样本可能受已装补丁影响。",
    IReadOnlyList<string>? EnabledFeatures = null,
    string? AttachSessionId = null, string? LastActionRequestId = null);

/// <summary>固定深度、固定字段、无遍历、无扫描、无写入、无远程执行。</summary>
public sealed class ReadOnlyProbe(IProbeMemory memory, AddressResolver resolver, string identity)
{
    public ProbeSample Capture(bool hooksInstalled, DateTimeOffset timestamp)
    {
        var fields = new Dictionary<string, ProbeField>();
        var identities = new List<(nint Address, uint Bits)>();
        // Head and tail often refer to the same unit. Read each field once per capture,
        // but always perform the final identity checks against fresh memory.
        var reads = new Dictionary<(nint Address, bool Floating, int Size), ProbeField>();
        var noSelection = false;
        var selection = Pointer("selection.root", resolver.Resolve("ra3_1.12.game+8DB73C"));
        var count = Read("selection.count", Add(selection, 0x5C));
        noSelection = count.Bits == 0;
        fields["selection.state"] = new(count.Value, noSelection ? "未选中" :
            count.Bits is null ? "选择链断裂" : "已选中");
        if (selection is not null && count.Bits is uint countBits)
            identities.Add((selection.Value + 0x5C, countBits));
        foreach (var (name, offset) in new[] { ("head", 0x50), ("tail", 0x54) })
        {
            var node = Pointer($"selection.{name}", noSelection ? null : Add(selection, offset));
            var wrapper = Pointer($"{name}.wrapper", Add(node, 8));
            var unit = Pointer($"{name}.unit", Add(wrapper, 0x138));
            Pointer($"{name}.template", Add(unit, 4));
            var health = Pointer($"{name}.health", Add(unit, 0x33C));
            foreach (var field in new[] { 4, 0xC, 0x10 })
                Read($"{name}.health.{field:X}", Add(health, field), true);
            var movement = Pointer($"{name}.movement", Add(unit, 0x374));
            var table = Pointer($"{name}.movement.table", Add(movement, 0x200));
            foreach (var (part, slot) in new[] { ("first", 0), ("second", 4) })
            {
                var component = Pointer($"{name}.movement.{part}", Add(table, slot));
                foreach (var field in slot == 0 ? new[] { 8, 0x40 } : new[] { 8, 0x40, 0x18 })
                    Read($"{name}.movement.{part}.{field:X}", Add(component, field), true);
            }
            Pointer($"{name}.level", Add(unit, 0x3CC));
            Pointer($"{name}.owner", Add(unit, 0x418));
            foreach (var field in new[] { 0x38, 0x3C, 0x40 })
                Read($"{name}.position.{field:X}", Add(unit, field), true);
        }
        Read("selected.typeCode", ResolveOptional("ra3_1.12.game+8E9838"));
        // Dump and screen-to-world callers show integer screen X/Y, not world floats.
        // +44 is retained as raw context bits, not interpreted as a coordinate.
        var mouse = Pointer("mouse.source", ResolveOptional("ra3_1.12.game+8DAEFC"));
        foreach (var field in new[] { 0x3C, 0x40, 0x44 })
            Read($"mouse.source.{field:X}", Add(mouse, field));
        foreach (var field in new[] { 0x1080, 0x1084, 0x1088 })
            Read($"coordinate.copy.{field:X}", hooksInstalled ? ResolveOptional($"MustCode+{field:X}") : null, true);
        foreach (var field in new[] { 8, 9, 0xA, 0xB, 0xC, 0xD, 0xE, 0xF, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x20 })
            Read($"flags.{field:X}", hooksInstalled ? ResolveOptional($"iEnable+{field:X}") : null, size: 1);
        Pointer("player.owner", hooksInstalled ? ResolveOptional("ID") : null);
        Read("reinforcement.type", hooksInstalled ? ResolveOptional("iEnable+24") : null);
        Read("reinforcement.count", hooksInstalled ? ResolveOptional("iEnable+28") : null);
        Read("reinforcement.rank", hooksInstalled ? ResolveOptional("iEnable+2C") : null);
        foreach (var name in new[] { "ammo.component", "danger.component", "ore.component", "zoom.current", "zoom.minimum", "zoom.maximum" })
            fields[name] = new(null, name.StartsWith("zoom.")
                ? "仅离线定位：在线采样未启用"
                : "未定位：仅有钩子寄存器或缺少已证明的根链，禁止猜偏移");
        var stable = selection is not null && identities.Distinct().All(item => Same(item.Address, item.Bits));
        return new(timestamp, identity, hooksInstalled, stable, fields);

        nint? ResolveOptional(string expression)
        {
            try { return resolver.Resolve(expression); }
            catch (KeyNotFoundException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        ProbeField Read(string name, nint? address, bool floating = false, int size = 4)
        {
            if (address is nint cachedAddress && reads.TryGetValue((cachedAddress, floating, size), out var cached))
            {
                fields[name] = cached;
                return cached;
            }
            ProbeField result;
            if (address is null) result = new(null, noSelection &&
                (name.StartsWith("head.") || name.StartsWith("tail.") || name is "selection.head" or "selection.tail")
                    ? "未选中" : "上游不可用");
            else try
            {
                var region = memory.Query(address.Value);
                if (!Readable(region, address.Value, size)) result = new(null, "字段不可读");
                else
                {
                    var bytes = memory.ReadBytes(address.Value, size);
                    if (bytes.Length != size) result = new(null, "短读");
                    else
                    {
                        uint bits = size == 1 ? bytes[0] : BitConverter.ToUInt32(bytes);
                        double value = floating ? BitConverter.UInt32BitsToSingle(bits) : bits;
                        result = new(double.IsFinite(value) ? value : null,
                            double.IsFinite(value) ? "可读（未认证合法）" : "非有限值", bits,
                            (ulong)(nuint)region!.BaseAddress, (ulong)region.RegionSize, region.Protect);
                    }
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ArgumentException)
            { result = new(null, "读取失败：" + ex.Message); }
            fields[name] = result;
            if (address is nint readAddress) reads[(readAddress, floating, size)] = result;
            return result;
        }

        nint? Pointer(string name, nint? address)
        {
            var value = Read(name, address);
            if (value.Bits is not uint bits) return null;
            if (bits == 0) { fields[name] = value with { Status = "空指针" }; return null; }
            // LAA x86 games on 64-bit Windows can have committed user pages above 2 GiB.
            // Query still must prove that the destination is readable; do not infer validity from range.
            if (bits < 0x10000 || bits > 0xFFFEFFFF || (bits & 3) != 0)
            { fields[name] = value with { Status = "指针越界或未对齐" }; return null; }
            try
            {
                var region = memory.Query((nint)bits);
                if (!Readable(region, (nint)bits, 4))
                { fields[name] = value with { Status = "指针落点不可读" }; return null; }
                fields[name] = value with { Status = "落点可读（类型未证明）", RegionBase = (ulong)(nuint)region!.BaseAddress,
                    RegionSize = (ulong)region.RegionSize, Protection = region.Protect };
                identities.Add((address!.Value, bits));
                return (nint)bits;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ArgumentException)
            { fields[name] = value with { Status = "落点查询失败：" + ex.Message }; return null; }
        }

        bool Same(nint address, uint bits)
        {
            try
            {
                if (!Readable(memory.Query(address), address, 4)) return false;
                var bytes = memory.ReadBytes(address, 4);
                return bytes.Length == 4 && BitConverter.ToUInt32(bytes) == bits;
            }
            // 与 Read 同一套过滤：Query/ReadBytes 也可能抛 ArgumentException（非法 size/地址），
            // 漏掉它会让整个快照因为一个字段而整体失败，而不是记成"这个字段无法验证"。
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or ArgumentException)
            { return false; }
        }
    }

    private static nint? Add(nint? pointer, int offset) => pointer is null ? null : pointer.Value + offset;

    internal static bool Readable(Win32MemoryRegion? region, nint address, int size)
    {
        var start = (ulong)(nuint)address;
        if (region is null || size <= 0 || start < 0x10000 || start + (uint)size > 0xFFFF0000UL ||
            region.State != 0x1000 || (region.Protect & 0x101) != 0 || (region.Protect & 0xEE) == 0) return false;
        var baseAddress = (ulong)(nuint)region.BaseAddress;
        return start >= baseAddress && start - baseAddress <= (ulong)region.RegionSize &&
            (ulong)size <= (ulong)region.RegionSize - (start - baseAddress);
    }
}
