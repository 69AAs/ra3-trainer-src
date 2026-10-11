# 第二十二轮

摘要（8行）
1. 已完成119槽候选表全量复制、逐槽校验及失败关闭；拒绝残缺表发布到设备。
2. 已加stdout无缓冲和安装/Clear进度；未加SEH。
3. 已验证781/781单测，失败0、跳过0；原233项原生契约全部通过。
4. 已验证新增20项真实安装路径测试；18槽短复制变异触发7项失败。
5. 已验证Windows绝对路径tar：PATH首位放故障tar、全新解压和构建仍成功。
6. 已验证8个产物二次构建及异目录全新工具链构建SHA256一致，见[完整哈希](SHA256SUMS.txt)。
7. 未验证真实GPU：本会话adapters=0、退出6；当前源码与旧EXE均已复制119槽，不能声称已证实或消除了用户堆栈的真实根因。
8. 独立Trellis check派发失败；无.git、Trellis脚本/技能缺失，未提交、未运行finish-work；主会话完成本地审查。

- **根因**：逐行核实修前`harness.cpp`已是`hostTable[119]`，`memcpy(...,sizeof(hostTable))`复制476字节。旧EXE SHA256为`1134E1CA6065BB0DEF6350EAF270E62C13EC25CB528FA0520546E9C1EFB6CC38`；[修前反汇编](out/round22/before-disassembly.txt)在`0x401f43`置`ecx=0x77`，`0x401f59`执行`rep movsl`，即119个DWORD。`Frame`在`0x4015cd`通过`0xac`偏移调用Clear[43]，下一指令`0x4015d3`与用户堆栈相差加载基址；`wmain`返回地址`0x401f82`亦吻合。证实“缺少安装前完整性校验”，未证实“数组小于119”；不能将假设当实际根因。驱动内部调用/具体崩溃指令仍需真实会话证据。
- **修复**：[device-table.h](device-table.h)绑定119/17/43常量，复制后替换Present，再扫描源表和候选表全部槽：无NULL、Present等于CapturePresent、118个其余槽逐项等于原表。通过后才保存原Present并发布vptr；失败只记日志、返回8，不调用Frame或可疑Release。`setvbuf(stdout,NULL,_IONBF,0)`在wmain首行；安装日志记录槽数、字节数、Present原/新指针、Clear及null=0。build固定系统tar，自动跑全部原生检查并输出8项完整SHA256；不改overlay.cpp、22个hook、动作派发或安全许可。
- **证据**：[单测TRX](out/round22/round22.trx)、[单测日志](out/round22/tests.log)、[原生构建/检查](out/round22/build-first.log)、[二次构建](out/round22/rebuild.txt)、[异目录干净构建](out/round22/clean-build.txt)、[tar污染PATH试验](out/round22/tar-portability.log)、[无适配器smoke](out/round22/smoke.log)。[保护范围校验](out/round22/protected-after.txt)：src/tests的188个源文件哈希未变。单测命令：`D:\dotnet\dotnet.exe test tests/Ra3Trainer.Tests/Ra3Trainer.Tests.csproj --artifacts-path tools/Dx9OverlayHarness/out/round22/dotnet --logger "trx;LogFileName=round22.trx" --results-directory tools/Dx9OverlayHarness/out/round22`。真机由用户在out目录重定向运行`Dx9OverlayHarness.exe --smoke > smoke-desktop.log 2>&1`，检查退出0与新frame.bmp；若仍崩，日志将区分安装前、Clear内或Present内，不能把合成通过当GPU通过。
- **新测试**：[harness-table-tests.cpp](harness-table-tests.cpp)直接编入实际harness.cpp，检查实际hostTable长度119/476字节、119槽非NULL、非Present逐项相等、Present替换/原指针保留；遍历119个位置注入NULL并确认拒绝且设备vptr不变，另拒绝18槽数组、补丁后NULL/错误非Present/未替换Present、空入口/设备/表。[短复制变异日志](out/round22/prefix-mutation.log)证明仅复制18槽导致20项中7项失败、退出1。**这条测试当初存在就能在本机拦住它——这里的“它”严格指短复制造成NULL槽的缺陷；不能诚实地声称它一定能拦住当前这份已经复制119槽的旧EXE所发生的真实GPU崩溃。** 独立check两次派发均报`no thread with id: 01a0f342-4538-73b3-9aa0-64df48826b47`，不标记该流程已通过；本轮新约定记入本目录README以遵守修改范围。
