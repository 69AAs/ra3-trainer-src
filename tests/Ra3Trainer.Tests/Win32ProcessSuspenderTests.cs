using System.Reflection;
using System.Runtime.InteropServices;
using Ra3Trainer.Core.Runtime;
using Xunit;

namespace Ra3Trainer.Tests;

public sealed class Win32ProcessSuspenderTests
{
    [Theory]
    [InlineData("OpenThread", "kernel32.dll", "OpenThread", true)]
    [InlineData("SuspendThread", "kernel32.dll", "SuspendThread", true)]
    [InlineData("ResumeThread", "kernel32.dll", "ResumeThread", true)]
    [InlineData("GetThreadContext", "kernel32.dll", "GetThreadContext", true)]
    [InlineData("NtQueryInformationThread", "ntdll.dll", "NtQueryInformationThread", false)]
    public void NativeMethodsBindToExportedEntryPoints(string methodName, string expectedLibrary,
        string expectedEntryPoint, bool expectedSetLastError)
    {
        var method = typeof(Win32ProcessSuspender).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var dllImport = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(dllImport);
        Assert.Equal(expectedLibrary, dllImport.Value);
        // 没写 EntryPoint 时入口点默认就是方法名，所以这里按"有效入口点"比较。
        Assert.Equal(expectedEntryPoint, dllImport.EntryPoint ?? methodName);
        Assert.True(dllImport.ExactSpelling, methodName + " 必须 ExactSpelling，否则会去找 A/W 变体");
        // SetLastError 丢了的话 Marshal.GetLastWin32Error() 读到的是脏值，错误报告会全部指错。
        Assert.Equal(expectedSetLastError, dllImport.SetLastError);
    }
}
