// 硬件断点探针（32 位原生版）：抓"谁在读/写某个地址"。
//
// 为什么要单独做一个 32 位助手：游戏是 32 位，从 64 位 Python 去设 DR 寄存器会被 WOW64 吃掉
// （实测 SetThreadContext 报成功、读回 Dr0 = 0）。32 位进程里操作 32 位目标，DR 原生生效。
//
// 纪律：无 GUI、不抢焦点、不动键鼠；目标进程只在每条命中的毫秒级停一下；**任何路径都恢复 DR 并脱钩**
// （finally + DebugSetProcessKillOnExit(false) 双保险）。
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class Program
{
    // ---- Win32 ----
    private const uint DBG_CONTINUE = 0x00010002;
    private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;
    private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    private const uint EXCEPTION_BREAKPOINT = 0x80000003;
    private const uint THREAD_ALL_ACCESS = 0x1F03FF;
    private const uint CONTEXT_i386 = 0x00010000;
    private const uint CONTEXT_CONTROL = 0x00010001;
    private const uint CONTEXT_INTEGER = 0x00010002;
    private const uint CONTEXT_DEBUG_REGISTERS = 0x00010010;
    private const uint CONTEXT_FULL_DR = CONTEXT_i386 | CONTEXT_DEBUG_REGISTERS | CONTEXT_CONTROL | CONTEXT_INTEGER;

    [StructLayout(LayoutKind.Sequential)]
    private struct Context
    {
        public uint ContextFlags;
        public uint Dr0, Dr1, Dr2, Dr3, Dr6, Dr7;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)] public byte[] FloatSave;
        public uint SegGs, SegFs, SegEs, SegDs;
        public uint Edi, Esi, Ebx, Edx, Ecx, Eax;
        public uint Ebp, Eip, SegCs, EFlags, Esp, SegSs;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)] public byte[] ExtendedRegisters;

        public static Context New() => new Context
        {
            ContextFlags = CONTEXT_FULL_DR,
            FloatSave = new byte[112],
            ExtendedRegisters = new byte[512],
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DebugEvent
    {
        public uint Code, Pid, Tid;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 160)] public byte[] Body;

        public static DebugEvent New() => new DebugEvent { Body = new byte[160] };

        /// <summary>异常事件的 ExceptionCode（union 首字段）。</summary>
        public uint ExceptionCode => Code == 1 ? BitConverter.ToUInt32(Body, 0) : 0;

        /// <summary>异常事件的 ExceptionAddress。</summary>
        public uint ExceptionAddress => Code == 1 ? BitConverter.ToUInt32(Body, 12) : 0;

        /// <summary>CREATE_PROCESS 事件里主线程句柄的位置（union+8）。</summary>
        public uint NewThreadId => Tid;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcess(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugActiveProcessStop(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WaitForDebugEvent(out DebugEvent e, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetThreadContext(IntPtr h, ref Context c);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetThreadContext(IntPtr h, ref Context c);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DebugSetProcessKillOnExit(bool killOnExit);

    private static readonly HashSet<uint> Armed = new HashSet<uint>();
    private static uint[] _addresses = Array.Empty<uint>();
    private static uint _dr7;
    private static bool _verbose;
    private static int _hits;
    private static readonly List<uint> HitEips = new List<uint>();
    private static readonly Dictionary<string, int> _byAddress = new Dictionary<string, int>();
    private static readonly Dictionary<uint, int> _byEip = new Dictionary<uint, int>();

    private static uint BuildDr7(bool writeOnly, int length, int slots)
    {
        uint rw = writeOnly ? 0b01u : 0b11u;            // 01=只写, 11=读/写
        uint len = length switch { 1 => 0b00u, 2 => 0b01u, _ => 0b11u };
        uint dr7 = 0;
        for (var slot = 0; slot < slots; slot++)
        {
            dr7 |= 1u << (2 * slot);                    // L0..L3 使能位（每槽两位：L/G）
            dr7 |= (rw | (len << 2)) << (16 + 4 * slot);  // 每槽 RW/LEN 在 16+4i 位
        }

        return dr7;
    }

    private static void Arm(uint tid)
    {
        if (Armed.Contains(tid)) return;
        var handle = OpenThread(THREAD_ALL_ACCESS, false, tid);
        if (handle == IntPtr.Zero) return;

        var context = Context.New();
        if (GetThreadContext(handle, ref context))
        {
            context.Dr0 = _addresses.Length > 0 ? _addresses[0] : 0;
            context.Dr1 = _addresses.Length > 1 ? _addresses[1] : 0;
            context.Dr2 = _addresses.Length > 2 ? _addresses[2] : 0;
            context.Dr3 = _addresses.Length > 3 ? _addresses[3] : 0;
            context.Dr7 = _dr7;
            if (SetThreadContext(handle, ref context))
            {
                var check = Context.New();
                if (GetThreadContext(handle, ref check) && check.Dr0 == context.Dr0 && check.Dr7 == _dr7)
                {
                    Armed.Add(tid);
                    if (_verbose) Console.WriteLine($"  · 线程 {tid} 装上并读回核对通过 (DR0=0x{check.Dr0:X8} DR7=0x{check.Dr7:X})");
                }
                else if (_verbose)
                {
                    Console.WriteLine($"  · 线程 {tid} 读回核对失败 (DR0=0x{check.Dr0:X8}) ⇒ 当没装");
                }
            }
        }

        CloseHandle(handle);
    }

    private static void DisarmAll()
    {
        foreach (var tid in Armed)
        {
            var handle = OpenThread(THREAD_ALL_ACCESS, false, tid);
            if (handle == IntPtr.Zero) continue;
            var context = Context.New();
            if (GetThreadContext(handle, ref context))
            {
                context.Dr0 = context.Dr1 = context.Dr2 = context.Dr3 = context.Dr6 = context.Dr7 = 0;
                SetThreadContext(handle, ref context);
            }

            CloseHandle(handle);
        }

        Console.WriteLine($"  · 已把 {Armed.Count} 个线程的调试寄存器清零");
        Armed.Clear();
    }

    /// <summary>盯住地址；返回命中次数。</summary>
    private static int Watch(uint pid, uint[] addresses, bool writeOnly, int length, double seconds, int maxHits)
    {
        _addresses = addresses.Take(4).ToArray();
        _dr7 = BuildDr7(writeOnly, length, _addresses.Length);
        _hits = 0;

        DebugSetProcessKillOnExit(false);
        if (!DebugActiveProcess(pid))
        {
            Console.WriteLine($"DebugActiveProcess 失败: {Marshal.GetLastWin32Error()}");
            return 0;
        }

        var events = new Dictionary<uint, int>();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        try
        {
            while (DateTime.UtcNow < deadline && _hits < maxHits)
            {
                if (!WaitForDebugEvent(out var e, 200)) continue;
                events[e.Code] = events.TryGetValue(e.Code, out var n) ? n + 1 : 1;

                if (e.Code == 3 || e.Code == 2) Arm(e.NewThreadId);        // 进程创建 / 线程创建（已有线程也会补发）

                if (e.Code == 1 && e.ExceptionCode == EXCEPTION_SINGLE_STEP)
                {
                    var handle = OpenThread(THREAD_ALL_ACCESS, false, e.NewThreadId);
                    if (handle != IntPtr.Zero)
                    {
                        var context = Context.New();
                        if (GetThreadContext(handle, ref context))
                        {
                            var slot = -1;
                            for (var i = 0; i < _addresses.Length && i < 4; i++)
                            {
                                if ((context.Dr6 & (1u << i)) != 0) { slot = i; break; }
                            }

                            var which = slot >= 0 && slot < _addresses.Length ? $"0x{_addresses[slot]:X8}" : "未知";
                            _hits++;
                            HitEips.Add(context.Eip);
                            _byAddress.TryGetValue(which, out var perAddress);
                            _byAddress[which] = perAddress + 1;
                            _byEip.TryGetValue(context.Eip, out var perEip);
                            _byEip[context.Eip] = perEip + 1;
                            if (_hits <= 40)
                            {
                                Console.WriteLine($"  命中 #{_hits} 地址={which}: EIP=0x{context.Eip:X8}"
                                                  + $" ecx=0x{context.Ecx:X8} edx=0x{context.Edx:X8} esi=0x{context.Esi:X8} edi=0x{context.Edi:X8}"
                                                  + $" eax=0x{context.Eax:X8}");
                            }

                            context.Dr6 = 0;                                  // 清掉标志位，否则会立刻再断
                            SetThreadContext(handle, ref context);
                        }

                        CloseHandle(handle);
                    }
                }

                uint status = e.Code != 1 ? DBG_CONTINUE
                    : (e.ExceptionCode == EXCEPTION_SINGLE_STEP || e.ExceptionCode == EXCEPTION_BREAKPOINT)
                        ? DBG_CONTINUE : DBG_EXCEPTION_NOT_HANDLED;
                ContinueDebugEvent(e.Pid, e.Tid, status);
            }
        }
        finally
        {
            DisarmAll();
            DebugActiveProcessStop(pid);
            Console.WriteLine($"  · 事件统计: " + string.Join(", ", events.Select(kv => $"{kv.Key}×{kv.Value}")));
            Console.WriteLine("  · 已脱钩（进程继续正常跑）");
        }

        return _hits;
    }

    private static int SelfTest(string target)
    {
        Console.WriteLine($"自测：起靶子 {target}");
        var child = Process.Start(new ProcessStartInfo(target)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        });
        try
        {
            var line = child.StandardOutput.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                Console.WriteLine("靶子没吐出地址 ⇒ 起不来（比如缺 .NET x86 运行时）");
                return 2;
            }

            var address = uint.Parse(line.Trim().TrimStart('0', 'x', 'X'), NumberStyles.HexNumber);
            Console.WriteLine($"靶子 pid={child.Id} 地址=0x{address:X8}；盯住它的写操作 3 秒……");
            _verbose = true;
            var hits = Watch((uint)child.Id, new[] { address }, writeOnly: true, length: 4, seconds: 3, maxHits: 5);
            Console.WriteLine($"自测结果：命中 {hits} 次 ⇒ {(hits > 0 ? "✓ 通过（探针可用）" : "✗ 没命中")}");
            Console.WriteLine("靶子还活着吗: " + !child.HasExited + "（必须 True：脱钩后不能把它弄死）");
            return hits > 0 && !child.HasExited ? 0 : 1;
        }
        finally
        {
            try { if (!child.HasExited) child.Kill(); } catch { }
        }
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 重定向时可能不允许 */ }

        string target = null;
        uint pid = 0;
        var addresses = new List<uint>();
        var writeOnly = false;
        var length = 4;
        var seconds = 10.0;
        var maxHits = 50;
        var selfTest = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid": pid = uint.Parse(args[++i]); break;
                case "--addr":
                    foreach (var piece in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        addresses.Add(Convert.ToUInt32(piece.Trim().Replace("0x", ""), 16));
                    }

                    break;
                case "--write": writeOnly = true; break;
                case "--len": length = int.Parse(args[++i]); break;
                case "--seconds": seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max": maxHits = int.Parse(args[++i]); break;
                case "--selftest": selfTest = true; if (i + 1 < args.Length) target = args[++i]; break;
                case "--verbose": _verbose = true; break;
            }
        }

        if (selfTest) return SelfTest(target);
        if (pid == 0 || addresses.Count == 0)
        {
            Console.WriteLine("用法: HwWatch --pid <pid> --addr 0xADDR [--write] [--len 4] [--seconds 10] [--max 50]");
            Console.WriteLine("      HwWatch --selftest <target.exe>");
            return 2;
        }

        Console.WriteLine($"盯住 pid={pid} 地址=[{string.Join(", ", addresses.Select(a => $"0x{a:X8}"))}]"
                          + $"（{(writeOnly ? "只写" : "读/写")}，{length} 字节），最多 {seconds} 秒……");
        _verbose = true;
        var hits = Watch(pid, addresses.ToArray(), writeOnly, length, seconds, maxHits);
        Console.WriteLine($"共命中 {hits} 次");
        return hits > 0 ? 0 : 1;
    }
}
