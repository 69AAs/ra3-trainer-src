using System;
using System.Threading;
using System.Runtime.InteropServices;
// 硬件断点探针的测试靶子（32 位）：把一个**固定地址**反复改写，好让 DR0 写断点必然命中。
// 只用来验证 tools/hw_watch.py（探针）在 32 位进程上能装/命中/脱钩，跟游戏无关。

var buffer = Marshal.AllocHGlobal(4);
Marshal.WriteInt32(buffer, 0);
Console.WriteLine($"0x{buffer.ToInt64():X}");
Console.Out.Flush();

var counter = 0;
while (true)
{
    Marshal.WriteInt32(buffer, ++counter);
    Thread.Sleep(1);
}
