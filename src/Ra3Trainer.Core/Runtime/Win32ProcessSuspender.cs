using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ra3Trainer.Core.Runtime;

public sealed class Win32ProcessSuspender : IProcessSuspender
{
    private const uint ThreadSuspendResume = 0x0002;
    private const uint SuspendFailed = 0xFFFFFFFF;

    public static Win32ProcessSuspender Instance { get; } = new();

    private Win32ProcessSuspender()
    {
    }

    public IDisposable Suspend(int processId)
    {
        var suspendedThreads = new List<SafeWaitHandle>();
        try
        {
            using var process = Process.GetProcessById(processId);
            var seen = new HashSet<int>();
            var stable = false;
            for (var pass = 0; pass < 8; pass++)
            {
                process.Refresh();
                var added = false;
                foreach (ProcessThread thread in process.Threads)
                {
                    if (!seen.Add(thread.Id)) continue;
                    added = true;
                    var handle = OpenThread(ThreadSuspendResume | 0x0008 | 0x0040, false, (uint)thread.Id);
                    if (handle.IsInvalid)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new Win32Exception(error, $"无法打开目标线程 {thread.Id}，已取消补丁操作。");
                    }
    
                    if (SuspendThread(handle) == SuspendFailed)
                    {
                        var error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new Win32Exception(error, $"无法暂停目标线程 {thread.Id}，已取消补丁操作。");
                    }
    
                    suspendedThreads.Add(handle);
                }
                if (!added) { stable = true; break; }
                }

            if (!stable)
                throw new InvalidOperationException("目标线程集合持续变化，已取消操作并恢复线程。");

            // 稳定那一趟之后又冒出来的线程，这里能看见（把窗口收窄到"最后一趟扫描之后"）。
            // 纯用户态没法真正冻结"全世界"，所以窗口里有变化就取消操作，绝不硬写代码。
            process.Refresh();
            foreach (ProcessThread thread in process.Threads)
            {
                if (!seen.Contains(thread.Id))
                {
                    throw new InvalidOperationException($"目标在挂起过程中创建了新线程 {thread.Id}，已取消操作并恢复线程。");
                }
            }

            if (suspendedThreads.Count == 0)
            {
                throw new InvalidOperationException($"No target threads could be suspended for process {processId}.");
            }

            return new SuspendedProcess(suspendedThreads);
        }
        catch (Exception suspendFailure)
        {
            try
            {
                ResumeAndDispose(suspendedThreads);
            }
            catch (Exception resumeFailure)
            {
                throw new AggregateException(suspendFailure, resumeFailure);
            }
            throw;
        }
    }

    private static void ResumeAndDispose(IReadOnlyList<SafeWaitHandle> suspendedThreads)
    {
        var failures = new List<Exception>();
        for (var index = suspendedThreads.Count - 1; index >= 0; index--)
        {
            var thread = suspendedThreads[index];
            if (ResumeThread(thread) == SuspendFailed)
            {
                var error = Marshal.GetLastWin32Error();
                failures.Add(new Win32Exception(error, "恢复目标线程失败。"));
            }
            thread.Dispose();
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("部分目标线程未能恢复运行。", failures);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenThread", SetLastError = true, ExactSpelling = true)]
    private static extern SafeWaitHandle OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", EntryPoint = "SuspendThread", SetLastError = true, ExactSpelling = true)]
    private static extern uint SuspendThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true, ExactSpelling = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool GetThreadContext(SafeWaitHandle thread, [In, Out] byte[] context);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationThread(SafeWaitHandle thread, int informationClass,
        [Out] byte[] information, int length, out int returnLength);

    private sealed class SuspendedProcess : IDisposable, IRemoteCodeQuiescence
    {
        private readonly IReadOnlyList<SafeWaitHandle> _suspendedThreads;
        private bool _disposed;

        public SuspendedProcess(IReadOnlyList<SafeWaitHandle> suspendedThreads)
        {
            _suspendedThreads = suspendedThreads;
        }

        public void EnsureUnreferenced(Ra3Trainer.Core.Memory.IProcessMemory memory, ulong address, ulong size)
        {
            if (_disposed || Environment.Is64BitProcess)
                throw new InvalidOperationException("无法验证 x86 线程静默，暂不释放远程代码；请使用 x86 修改器重试全撤。");
            foreach (var thread in _suspendedThreads)
            {
                // x86 CONTEXT is 716 bytes. CONTROL | INTEGER captures EIP, ESP and all general registers.
                var context = new byte[716];
                BitConverter.GetBytes(0x10003u).CopyTo(context, 0);
                if (!GetThreadContext(thread, context))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "读取暂停线程上下文失败，暂不释放远程代码。");
                foreach (var offset in new[] { 156, 160, 164, 168, 172, 176, 180, 184, 196 })
                    RejectReference(BitConverter.ToUInt32(context, offset), address, size);
                var information = new byte[28]; // THREAD_BASIC_INFORMATION for this x86 caller.
                var status = NtQueryInformationThread(thread, 0, information, information.Length, out var returned);
                if (status < 0 || returned != information.Length)
                    throw new InvalidOperationException($"读取线程栈边界失败（NTSTATUS 0x{status:X8}），暂不释放远程代码。");
                var teb = BitConverter.ToUInt32(information, 4);
                var stackBase = BitConverter.ToUInt32(memory.ReadBytes(unchecked((nint)(teb + 4)), 4));
                var stack = BitConverter.ToUInt32(context, 196);
                if (stack < 0x10000 || stackBase < stack || (ulong)stackBase - stack > 64UL * 1024 * 1024)
                    throw new InvalidOperationException("线程栈边界无效，暂不释放远程代码。");
                // Include saved return addresses and saved registers, not just current EIP.
                while (stack < stackBase)
                {
                    var length = (int)Math.Min(4096u, stackBase - stack);
                    var bytes = memory.ReadBytes(unchecked((nint)stack), length);
                    for (var offset = 0; offset <= bytes.Length - 4; offset++)
                        RejectReference(BitConverter.ToUInt32(bytes, offset), address, size);
                    if (length <= 3) break;
                    stack += (uint)(length - 3); // overlap catches pointers crossing a chunk boundary
                }
            }
        }

        private static void RejectReference(uint value, ulong address, ulong size)
        {
            if (value >= address && value - address < size)
                throw new InvalidOperationException("线程仍引用远程代码或数据，已恢复 Hook 但暂缓释放；请稍后重试一键全撤。");
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ResumeAndDispose(_suspendedThreads);
        }
    }
}
