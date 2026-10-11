# 离线 D3D9 覆盖层原型

**本目录顶层 out/Ra3Overlay.dll 仍仅用于离线验证台，不能复制到游戏目录。** Round25 新增独立 `out/product/Ra3Overlay.dll`，由 App 嵌入并按条件自动部署；无需手工复制。产品接线及未验证边界见 [第二十五轮报告](../../docs/round25-report.md)。原型历史见 [第二十三轮报告](ROUND23.md)、[第二十二轮报告](ROUND22.md)及 [第二十一轮报告](../../docs/round21-findings.md)。

## 一条命令构建

在仓库根目录的Windows PowerShell：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Dx9OverlayHarness/build.ps1
```

首次下载便携编译器（约89MiB，展开更大），无需管理员权限，不更改全局PATH。需要Windows自带curl/tar及网络；离线可预先放置同SHA256的`.toolchain/compiler.7z`。固定GCC14.2.0、mingw-w64 runtime12、i686/Win32/DWARF/MSVCRT，SHA256为`4A8A3F58C88EC6FF6F0270913F01BF2C0BC1366C39AFDA32BE05BC0D3BAF5126`。脚本验证包、编译x86、检查PE导入，不会启动游戏或自动运行注入。ExecutionPolicy只针对此PowerShell进程。

产物在`out/`：Ra3Overlay.dll、Dx9OverlayHarness.exe、OfflineLoader.exe、ContractTests.exe、HarnessTableTests.exe、CodeHookTests.exe、AbiProbe.exe；完整哈希写入`out/SHA256SUMS.txt`。构建自动执行 ABI 探针、20 项验证台表检查、原有233项契约检查及61项代码钩子检查，任一失败则构建失败。解压固定使用`$env:SystemRoot/System32/tar.exe`，不受 PATH 中 MSYS tar 影响。运行时只依赖Windows系统组件。编译器不是需要朋友安装的运行库。`out/proxy-probe/d3d9.dll`是无D3D功能的加载搜索标记，必须保持隔离。

## 用户手工验收（只用自建窗口）

1. 在登录后的**交互桌面会话**，不用管理员权限，执行`out\Dx9OverlayHarness.exe --smoke`。应退出0，所有PASS；frame.bmp保存在当前目录，应包含蓝底与左上深色细条中文。若adapters=0，环境不适用，不能记成功。当前自动执行环境正是这种情况。
2. 执行`out\Dx9OverlayHarness.exe --manual`。窗口初次显示不主动激活；玩家自己点击窗口后应看到「RA3 状态：离线覆盖层原型 | 已连接 | 功能未启用」。这行是固定演示文本，不是从游戏读取的真实状态。
3. 用户按O：文字消失；再按O：文字回来。重复20次；无崩溃、无重复文字、无明显资源增长。R触发同线程Reset，文字继续可见。
4. 用户自行按Alt+Enter，请求`Windowed=0`和当前显示模式；日志Reset返回0，文字仍在。再按Alt+Enter回窗口化。重复10次。全屏失败即停绘制，不强行改驱动设置。Windowed=0仅证明API请求模式，Windows全屏优化仍可能影响实际呈现；“覆盖独占”仍需在实际独占路径观察确认。
5. 用户自行Alt+Tab、最小化/恢复、移动到另一显示器或切换显示配置：设备丢失后OSD可以关闭，原窗口应可恢复、可退出。恢复后按O重新连接。记录Reset/Present HRESULT，不能把黑屏/冻结算通过。脚本不代按、不动鼠标。
6. 执行`out\OfflineLoader.exe`（无参数）：它只新建自己的隐藏验证台子进程。期望LoadLibrary三项均1、child-exit=0；硬件不可用时child-exit=6且加载器返回1，保留失败。检查ContractTests与真实smoke日志分别保存，不能混算。
7. 独立执行`out\ContractTests.exe`应显示`checks=233 failures=0`；`out\AbiProbe.exe`输出x86与槽位。这些即使在无GPU会话也能运行，**不替代步骤1–5**。
8. 关闭窗口：正常退出，DLL随宿主关闭。无需RTSS/D3DX；没有全局输入钩子。保留OS/显卡/驱动版本、日志、frame.bmp和每一步人工观察（通过/失败/未测）。

## 将来RA3 1.12验收门槛（本轮不执行）

先完成真实设备发现、SEH/故障隔离评估、非合作线程静默证明、App独立OSD编排和独立check；当前二进制不能直接进行此验收。

届时先离线校验实际RA3_1.12.game为PE32、版本1.12、导入/延迟导入与是否从system32绝对加载D3D9；取得对应启动器UseDSoundDll/EnableAutoHook实际调用链。仅凭开关名或导入表没有d3d9不能判否（可能动态加载）。同名DLL先核实所有权，不覆盖；保留配置与文件哈希。

用户后续自己连接游戏时，期望**无需额外安装、无需复制文件**就出现来自已确认状态快照的一行文字；断开后文字消失。一键全撤必须在OSD缺失、失效或禁用留驻时仍恢复原22站点。RA3原版/日冕分别验收，不能互相代替。没有真实呈现与回调退出证据就只记“未验证”，不能强卸。

## 宿主ABI约束

**架构不变式：验证台与DLL不得直接写入d3d9交付的设备对象任何字节，包括vptr；也不写原vtable。** AddRef/Release等合法COM方法由驱动自己管理内部状态，不属于补丁写入。119槽快照只用于校验/诊断，不发布到设备；原完整性辅助函数仅保留作回归测试。

捕获层从实际表[17]读Present地址，DLL从同一实际表[17]/[16]读Present/Reset地址，使用code-hook.h在函数入口放E9。DLL的Present蹦床会转入捕获层，捕获层蹦床再执行原驱动入口。恢复顺序为DLL两入口→卸载DLL→捕获层入口；每一层只恢复自己保存的字节。不会把device指针替换成包装器。

解码器为MinHook v1.3.4所带HDE32（保留许可证、源码哈希）；**不是引入完整MinHook，也不声称支持任意x86序言**。白名单只接受无前缀的整数序言和入口E9 rel32；按完整指令累计至少5字节，普通指令原样执行并跳回，E9重新计算位移。call、短跳、Jcc、loop、FPU、未知指令及跨不可读页一律拒装并输出所见字节，不截断/猜测长度。蹦床RW分配、写入后转RX；代码页临时RWX，检查还原属性、FlushInstructionCache、memcmp。

宿主只在创建/呈现线程且所有调用源封闭的边界Start/Stop。**函数代码为多个设备/接口共享，静默证明必须覆盖所有这些调用源，包括已缓存函数指针。** 本验证台控制自己的调用并join测试worker；没有为非合作进程实现通用线程冻结。回调计数零不能单独证明安全。其他设备调用只透传，不修改本设备覆盖层状态。

OverlayStop(FALSE)禁用并拒绝卸载；TRUE也必须检查owner、在途回调、设备vptr仍属原主、代码仍等于本层patch（或已经恢复的原字节）。安装一半失败会回滚两入口；任何还原/页属性/缓存刷新证据不足，保留禁用DLL及蹦床至获得真实静默证明或宿主退出。宿主收到Start失败后也先Stop确认，不能直接FreeLibrary。禁止SEH掩盖故障。

探针在第一次Clear之前输出对象和vtable地址、Present/Reset/Clear真实指针与原始32字节、QI(Ex) HRESULT、公开ABI槽数119/134、Ex对象/表是否相同、Ex的119..133槽，以及原表119..140处只读raw words。后者只证明这些地址可读，**不证明是有效槽，更不是私有槽位总数**；未知长度打印private-total=unknown。QI失败不能推导驱动内部只有119项。真实槽总数无法从COM接口可靠反射，不会编造这个数字。

维护约定：ABI经abi-probe.cpp绑定头文件sizeof/offsetof。20项检查保持数量及NULL/短表/逐槽完整性覆盖；其中原来“发布新vptr”三项安装断言改为“整个对象不变、真实蹦床语义、原槽不变且函数体已打补丁”，与本轮不变式一致。新增测试把整页设备及160槽表设为只读，分别运行实际捕获和实际DLL安装/还原，memcmp4096字节；执行蹦床、验证嵌套还原、模拟API失败与外部改码。用户限定改动范围，新约定只落本目录，不改.trellis/spec。
