# 第二十三轮：函数地址钩子

摘要（10行）
1. 捕获层与覆盖层均已移除设备vptr写入；仅修改真实Present/Reset函数入口。
2. 不写原vtable；设备对象与160槽表整页设只读时，实际捕获/实际DLL安装和还原均通过。
3. HDE32解码完整指令；支持安全整数序言与入口E9重定位，未知语义拒装，不用SEH。
4. 原781单测、233原生契约、20项回归全部通过；新增61项可执行/失败注入检查通过，无Skip。
5. 20项中三个旧“换表”安装断言迁移到本轮不变式，未删除完整性检查；不能声称旧换表断言原封不动通过。
6. 探针在Clear前输出真实地址/原字节、QI Ex结果、119/134公开槽数与扩展区raw words；私有总长度诚实标unknown。
7. 两次构建及异目录独立解压工具链构建，全部9个PE产物SHA256一致。
8. src/tests的170个受保护源码/项目/配置文件哈希不变；改动仅在tools/Dx9OverlayHarness内。
9. 本会话adapters=0、CreateDevice失败、退出6；真实Clear/Present/Reset、frame.bmp仍由用户真机验证。
10. 独立Trellis check派发失败；无.git、Trellis脚本/技能，未提交或运行finish-work；已完成本地主会话审查。

## 机制变更

[code-hook.h](code-hook.h)按设备真实表的函数地址建蹦床；[harness.cpp](harness.cpp)捕获Present，[overlay.cpp](overlay.cpp)钩Present/Reset。两层均不写设备内存、原表或设备vptr。快照hostTable不再发布。驱动通过合法COM方法管理自己的状态与AddRef/Release不属于补丁直接写入。

采用[MinHook v1.3.4的HDE32](https://github.com/TsudaKageyu/minhook/tree/v1.3.4/src/hde)解码，源码/许可证/哈希一并提供；**未引入完整MinHook**。白名单支持push/pop、NOP、常见mov/lea/test/xor/整数立即运算等无前缀指令；复制完整指令直到至少5字节，再E9跳回。入口E9重新计算目标位移。call、Jcc、loop、短跳、间接控制转移、FPU、前缀及未知指令拒装；不假定任意5字节可搬移。蹦床RW→RX，补丁页临时RWX→原属性；检查FlushInstructionCache、页属性与逐字节相等。

两条候选均评估：函数钩子不依赖对象私有表长度，选择此路；“完整换表”即使QI Ex成功也只能证明公开接口134槽，不能证明驱动私有表到此结束，且仍违背本轮禁止修改vptr的不变式，因此不采用，也不填空/猜ABI安全桩。Ex继承普通设备接口可见[微软接口文档](https://learn.microsoft.com/en-us/windows/win32/api/d3d9/nn-d3d9-idirect3ddevice9ex)，119/134具体槽数由本工具链头文件静态断言验证。

用户堆栈证实Clear内部空地址调用，119槽副本完整；“Ex私有槽越界”是合理机制，但现有堆栈缺少故障前间接调用的反汇编/寄存器，不能称唯一已证实根因。本改动彻底移除了两处截断对象表的机制。

## 证据

- [781单测日志](out/round23/tests.log)、[TRX](out/round23/round23.trx)：781通过、0失败、0跳过。
- [最终构建与检查](out/round23/build-first.log)：233原生契约、20回归、61新增均failures=0；编译-Wall -Wextra -Werror，PE i386及依赖检查通过。
- 233项原生契约条件不变，只把“install private vtable”标签改成“install function hooks”。20项仍覆盖NULL遍历119槽、短表拒绝、错误Clear/非Present/未改Present候选拒绝；三个安装断言从“发布新表/原指针直接调用/表槽替换”改成“全对象不变/实际蹦床执行原语义/表槽不变而代码已改”，这是本轮契约迁移，不是宣称旧换表行为仍成立。
- 61新增项执行常见序言、SIB、E9重定位、两个不同目标的嵌套钩子；拒绝不支持控制流/跨不可读页；模拟VirtualProtect及FlushInstructionCache失败；外部改码拒绝覆盖；实际DLL保持共享地址的其他设备透传；只读设备/160槽表4096字节memcmp。
- [重复构建](out/round23/rebuild.txt)、[异目录结果](out/round23/clean-build.txt)、[异目录构建日志](out/round23/build-clean.log)、[全部9个产物SHA256](SHA256SUMS.txt)。build.ps1同时固定编译器包和HDE源码哈希，关闭PE时间戳。
- [保护范围结果](out/round23/protected-result.txt)、[前](out/round23/protected-before.txt)/[后](out/round23/protected-after.txt)源码哈希一致；不改22 hook、派发、安全许可。

## 真机探针输出

在真实交互会话，从一个新输出目录执行out/Dx9OverlayHarness.exe --smoke并重定向stdout/stderr；DLL仍从EXE所在目录定位，frame.bmp写入当前目录。不要用旧frame.bmp充当本轮证据。

```powershell
$harness = (Resolve-Path tools/Dx9OverlayHarness/out/Dx9OverlayHarness.exe).Path
$run = Join-Path (Split-Path $harness) ('desktop-' + (Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory $run | Out-Null
Push-Location $run
try { & $harness --smoke > smoke-desktop.log 2>&1; "exit=$LASTEXITCODE" | Add-Content smoke-desktop.log }
finally { Pop-Location }
```

以下是**输出格式，不是伪造真机结果**：

```text
probe device=<ptr> vtable=<ptr> Present[17]=<ptr> Reset[16]=<ptr> Clear[43]=<ptr>
probe original slot=17/16/43 address=<ptr> first32=<64 hex chars or unreadable>
probe base-tail[119..140] readable=0/1 raw=<ptr> semantic=unknown
probe QI(IDirect3DDevice9Ex)=<HRESULT> base-interface-slots=119 Ex-interface-slots=134/0 private-total=unknown
probe Ex-device=<ptr> Ex-vtable=<ptr> same-object=0/1
probe Ex[119..133]=<ptr>  (only on successful QI)
hook original address=<ptr> length=<whole instructions> bytes=<saved hex>
hook planned address=<ptr> length=<same> bytes=<E9 and padding>
hook installed ...
Frame: entering Clear[43]
Frame: Clear=<HRESULT>; next Present[17]
hook restored byte-equal address=<ptr> length=<same> bytes=<exact saved hex>
RESULT checks=<n> failures=0
```

QI结果和134只描述公开Ex接口；公开COM没有“实现对象真实私有vtable总长度”反射接口。只读119..140处raw words可帮助对照，但可读、非空、像函数地址均不能证明槽有效；本轮明确保留这一限制而不盲目扫描到0。QI返回不同对象指针时打印两张表，不把Ex的槽数强套到原指针。

本机实际输出仅为[adapters=0、CreateDevice=0x8876086c、exit=6](out/round23/smoke-local.log)，没有真实设备探针数据。

## 还原证明

每层在发布前保存目标完整被覆盖指令字节和原页属性，准备两入口均通过后才安装DLL。停止先禁用绘制；只有宿主关闭所有共享函数调用来源、join所有worker、owner线程、callbacks=0、设备vptr未被外部替换时，才检查原地字节所有权并恢复。普通计数零不构成静默证明，也没有声称解决非合作进程线程冻结。

已装的目标若不等于自身patch或已恢复的before，拒绝覆盖，保持模块/蹦床留驻。部分安装失败回滚两个入口；改页属性、刷新缓存、字节或页属性验证失败均不能释放模块。宿主Start失败也必须先Stop成功才FreeLibrary。恢复顺序：DLL Reset/Present→卸载DLL→捕获层Present；每层打印并memcmp自己的完整原值，最后捕获层恢复驱动初值。新增测试执行还原后的原函数并核对保护属性，实际DLL逐字节核对两个函数体。

合成日志中的代表性还原值（地址随ASLR变化）：Present `b868087688`、Reset `b800000000`；日志每次同时给安装前原值与恢复值。真实驱动字节必须由上面的桌面日志给出，不能拿这些合成常量代替。

## 仍未验证项

真实显卡上Clear/Present/Reset的运行、中文frame.bmp、独占/设备丢失和真实序言是否被白名单支持均未验证；未知序言会拒装并打印字节，不能把合成成功当真机成功。未附加游戏、不抢焦点、不注入输入、不动鼠标、不加SEH。

Trellis implement两次、check一次派发均报`no thread with id: 01a0f34c-a4ba-78a3-99d0-dae9cb5a0978`，不标记独立check已通过。项目无.git、.trellis/scripts、.agents/skills、.codex/agents；无法提交或执行finish-work。本地主会话已复核指令白名单、双层顺序、失败留驻、对象内存不写及全部测试；因用户范围限制，新增约定写README而不修改.trellis/spec。
