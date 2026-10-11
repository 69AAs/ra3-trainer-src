using System;
using System.Runtime.InteropServices;
using System.Threading;

// 硬件断点/代码扫描自检的**靶子**：必须是 32 位进程（游戏 ra3_1.12.game 就是 32 位）。
class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint allocType, uint protect);

    static int Main()
    {
        IntPtr buf = Marshal.AllocHGlobal(4096);

        // 一页可执行内存，摆上 6 条已知模式（含我们载荷那条 89 83 18 04 00 00）
        IntPtr code = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x1000, 0x3000, 0x40); // COMMIT|RESERVE, EXECUTE_READWRITE
        byte[] page = new byte[0x1000];
        for (int i = 0; i < page.Length; i++) page[i] = 0x90;
        Put(page, 0x40, "898318040000");   // mov [ebx+418h], eax  ← 我们载荷里的那条
        Put(page, 0x80, "899114040000");   // mov [ecx+414h], edx
        Put(page, 0xC0, "898E14040000");   // mov [esi+414h], ecx
        Put(page, 0x100, "C7801404000078563412"); // mov dword [eax+414h], imm32
        Put(page, 0x140, "89940414040000");       // mov [eax+ecx*1+414h], edx
        Put(page, 0x180, "8B8318040000");         // mov eax,[ebx+418h]（只读）
        Marshal.Copy(page, 0, code, page.Length);

        Console.WriteLine("PID=" + Environment.ProcessId + " ADDR=0x" + buf.ToInt64().ToString("X8")
                          + " IS64=" + Environment.Is64BitProcess + " CODE=0x" + code.ToInt64().ToString("X8"));
        Console.Out.Flush();
        int n = 0;
        while (true) { Marshal.WriteInt32(buf, n++); Thread.Sleep(200); }
    }

    static void Put(byte[] page, int off, string hex)
    {
        for (int i = 0; i < hex.Length; i += 2)
            page[off + i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
    }
}
