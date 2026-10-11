# map-lib 脚本注入器（把 Lua 导入进 .map）

`tools/map-lib/inject-map-script/` —— 一个**可重复执行**的命令行工具，把一段 Lua 脚本"导入进"
RA3 日冕 mod 的 `.map`（写进地图的 `PlayerScriptsList`，等价于编辑器插件 `RA3MapUtil_LuaImporter`
做的事），**完全不改动源图**，并自证脚本明文确实进了输出文件。

---

## 0. ★最重要的一条坑：必须"原地替换 / 深拷贝"，**不要从零新建脚本组**★

旧版本这个工具（以及编辑器插件 `RA3MapUtil_LuaImporter` 本身）的做法是：

```csharp
new Script(); new ScriptGroup(); new ScriptAction();
scriptGroup.registerSelf(context);
scriptList.scriptGroups.Add(scriptGroup);
```

实测结果：**这样写出来的图进游戏后整组脚本毫无效果**（顶部按钮什么都不出现），哪怕脚本文本
确实写进了文件。原因是脚本区是一个"**嵌套资产图**"，里面每个对象都带着一串从解析器继承来的
隐式字段/开关与**名字池索引**；凭空 `new` 出来的对象这些字段全是默认值，游戏整组跳过。

**正确做法（本工具现在的两条路线）：把已经存在、游戏已经认得的那套对象当成模板用。**

| 命令 | 干什么 | 用在什么图 |
|---|---|---|
| `replace` | **原地替换**：按组名找到已存在的组、按脚本名找到已存在的 Script，**只换承载 lua 的那个 action 参数的字符串**。组 / 脚本 / 条件对象及其所有字段（`IsActive`/`isActive`/`DeactivateUponSuccess`/`ActiveInEasy|Medium|Hard`/`EvaluationInterval`/`LoopActions`/`id`/`nameIndex` …）**一字不动**。 | 图里**已经有**脚本组（例如官方例子图、已经注入过的图） |
| `add` | **深拷贝**：把一个已存在的脚本组整棵子树按字段深拷贝，只改组名/（目标）脚本名，再换内容，插到同一 ScriptList。可加 `--template-map <map>` 从**另一张图**里拷模板组。 | 图里**一个脚本组都没有**（例如纯遭遇战图） |

---

## 1. 怎么用

```bash
cd tools/map-lib/inject-map-script
./build.sh                          # 编译 x86 注入器（首次 / 改过源码后）
cd MapTool && ./build.sh && cd ..   # 编译 net8 验证/落盘工具（首次）

# 原地替换
./inject-map-script.sh replace <源.map> <输出.map> <脚本.lua> [组名] [脚本名]

# 深拷贝一个已存在的组（可从别的图拷模板）
./inject-map-script.sh add     <源.map> <输出.map> <脚本.lua> <被拷组名> [新组名] [新脚本名] [extra...]
REPAIR=1 ./inject-map-script.sh add ...          # 某些图需要（见 §5.3）
```

真实例子（本次验证用的就是这三条）：

```bash
# ① 原地替换：官方例子图 R3Training（组 Example_origin_05_top_button / 脚本 init.lua）
./inject-map-script.sh replace \
  "…/日冕地图lua例子/使用原生api （新手推荐）/Example_origin_05_top_button/Example_origin_05_top_button.map" \
  out/R3Training.inplace.map ../probe.lua Example_origin_05_top_button init.lua

# ② 从模板图深拷贝一个组，注入一张自己没有任何脚本组的遭遇战图
./inject-map-script.sh add \
  "C:/Users/user/AppData/Roaming/Red Alert 3/Maps/hs_1v5_test/1v5(v1.3).map" \
  out/hs_1v5_test.HSTest.map ../probe.lua Example_origin_05_top_button HSTest init.lua \
  --template-map "…/artifacts/_rollback/R3Training.map.orig-20261007-152852"

# ③ 深拷贝该图自带的一个组（不碰它原有脚本），并修复 MapCoreLib 往返写坏的资产
REPAIR=1 ./inject-map-script.sh add \
  "C:/Users/user/AppData/Roaming/Red Alert 3/Maps/超爽单人防守地图1/超爽单人防守地图1.map" \
  out/超爽防守.add.map ../probe.lua "摇光这能放这里刷，否则模型消失" R3MapLib init.lua
```

产物：

- `out/xxx.map` —— 最终 .map（压缩，和磁盘上所有官方图一样以 `EAR\0` 开头）
- `out/xxx.map.raw` —— **未压缩**副本（`CkMp` 开头），专门用来按字节 grep 脚本明文
- `out/.inject-work/fields-out.txt` —— 目标组整棵子树的**逐字段表**（用于对照）
- 控制台报告 + `out/.inject-work/inject.log`

### 直接调 x86 注入器（细节开关）

```
InjectMapScript.exe dump    <map> [组名子串] [--fields]           # 看脚本组结构/反射字段
InjectMapScript.exe fields  <map> <组名|#下标> [--out FILE]        # 输出"路径=值"逐字段表
InjectMapScript.exe replace <src.map> <out.map> <lua> [组名] [脚本名] [--redundancy N] [--gbk] [--raw] [--dump-fields FILE]
InjectMapScript.exe add     <src.map> <out.map> <lua> <被拷组名> [新组名] [新脚本名] [--template-map MAP] [--src-script NAME]
                            [--keep-other-scripts] [--list-index N] [--dump-fields FILE] [--redundancy N] [--gbk] [--raw]
```

- `--redundancy N`：尾部 `-- end of script, please ignore this line` 占位条数，默认 100（与官方例子图一致）。
- `--keep-other-scripts`：add 时**保留**被拷组里其它脚本的原内容（默认是把它们内容清空成占位，字段不变）。
- `--list-index N`：add 时插到第 N 个 ScriptList（默认 0）。
- `--gbk`：源 lua 文件按 GBK 读（默认按 UTF-8；写进图里一律是 GBK 字节）。
- `--dump-fields FILE`：把**改完之后**目标组的逐字段表写出来（用于对照）。

---

## 2. 两个可执行文件，为什么是两个

| 组件 | 运行时 | 引用 | 干什么 |
|---|---|---|---|
| `bin/InjectMapScript.exe` | .NET Framework 4.x / **x86** | `NewWorldBuilder\bin\MapCoreLib.dll` | 解析 `.map` → 替换/深拷贝脚本组 → 另存 |
| `MapTool/bin/Release/net8.0/MapTool.dll` | .NET 8 / x64 | 地编伴侣自带的 `Dreamness.RA3.Map.Parser.dll` | 打开/校验/另存；生成未压缩副本；`slinfo`/`reflect` 调试 |

`MapCoreLib.dll` 是 32BITREQUIRED + 非 ILOnly 的 .NET Framework 程序集，只能在 x86 的 .NET
Framework 进程里跑；而地编伴侣那套解析器是 net8 的。脚本内容的二进制格式只有 MapCoreLib 会写，
所以注入用前者；**校验只能用后者** —— 见 §5.1。

`repair-roundtrip.py`、`dev/ReflectDump.cs` 是辅助脚本：前者修 MapCoreLib 往返写坏的资产字节，
后者是反射探针（查 MapCoreLib 内部类型/字段，调试用）。

---

## 3. 脚本区的两个格式陷阱（都实测踩到并修好，改代码前先看这段）

### 3.1 嵌套资产的"第一个字段"是**名字池下标**，不是普通 id

脚本区里每个嵌套资产（`ScriptGroup` / `Script` / `ScriptAction` / `OrCondition` / `Condition`）
在文件里写成 `[第一个字段:int32][version:int16][dataSize:int32][数据]`。那个"第一个字段"在
MapCoreLib 里叫 `id`，**语义其实是"这个资产的资产名（ScriptGroup/Script/…）在 stringPool 里的下标"**。

后果：**跨图深拷贝时必须按目标图的池子重映射**这些 `id`（以及 `ScriptContent.nameIndex`）。
不重映射的话解析器拿模板图的下标去目标图的池子里查，查到的是别的名字 → **整组读不出来**
（地编伴侣报 `groups=0`；如果恰好查到 "Script"，这个组还会被当成脚本读成 `topScripts=1`）。
工具里对应 `RemapAssetIds()` / `SyncNameIndexes()`。

### 3.2 `RegisterString(str)` 会给新名字分配重复下标 —— 必须显式给下标，且要早于 `ScriptAction.of`

`Ra3MapStruct.RegisterString(str)`（单参）实测会给新名字返回一个**与既有最大下标相同**的索引：

```
目标图原池子 76 条(下标 1..76，WaypointsList=76)
RegisterString("DEBUG_MESSAGE_BOX")  -> 76     ← 与 WaypointsList 撞车！
写盘后 WaypointsList 被顶掉、既有资产名索引整体错位
```

必须用 `RegisterString(str, index)` 显式递增分配（工具里 `RegisterNamesIntoPool()`）。
而且**要在任何 `ScriptAction.of(...)` 之前**做完 —— `.of` 内部就会调那个坏重载去注册 contentName。

---

## 4. 挂载形态（导入之后）

`.map` 里已经有脚本了；地图旁边那个同名文件夹里的 `init.lua` 是**源文件**，`install-map-lib.ps1`
负责装/卸它。改 `init.lua` 不改 `.map`（真机已验收：改完立刻生效）。要换 .map 里的脚本内容再跑一次
本工具即可（`replace` 幂等；`add` 同名组会报错，需换个新组名）。

---

## 5. 已知限制（都实测过）

### 5.1 MapCoreLib 的解析器读不了"自己写出来的"脚本区（长内容时）

`MapCoreLib.Core.Ra3Map.parse()` 在脚本资产处会无限刷 `unknown assetName: …` 并卡死（实测日志能
刷到 4.6 GB/分钟）。读官方 WorldBuilder 写的图没问题；读它自己写的图，**内容短**还行、**内容长**
就挂。纯 round-trip（不做任何注入）写出来的图也一样挂 —— 所以这是**库自身的读写不对称**，不是注入
引入的。

影响与对策：

- **不能用 `InjectMapScript.exe fields/dump` 去读输出图**。字段对照表的"输出侧"改用
  `--dump-fields`：注入器在**改完内存对象、写完盘之后**直接把目标组整棵子树的字段打出来
  （就是刚写进文件的那份对象状态），再用「地编伴侣解析器」校验文件本身。
- `LogUtil.sw` 上挂了"只保留前 400 行"的 writer，防刷爆磁盘；`runbounded.sh` 用来跑 N 秒强杀。

### 5.2 两个库的贴图表

`Ra3MapFacade.NewMap(..., defaultTexture)` 需要一张由地编伴侣在自己进程里注册过的贴图表，
独立进程调会 `KeyNotFoundException: … 'Grass' …`（`MapTool newmap` 会打印这条）。所以
**从零新建地图**目前做不了（要造测试图就克隆现成遭遇战图，见 §7）。

### 5.3 MapCoreLib 会把某些图的某些资产"写坏"（Teams / ObjectsList）

实测：`超爽单人防守地图1.map` 用 MapCoreLib 纯 round-trip，Teams/ObjectsList 的**内容字节**会被
改坏（未压缩流里有成片 `ff -> 0f` 的位翻转），「地编伴侣」解析器给它们标 `errored=True`
（dataSize 不变）。同样一张图，用 `MapTool resave`（Dreamness）走一遍是干净的
（`errored=False`）。**这是 MapCoreLib 的既有缺陷，跟注入无关**（不注入也复现），而且
MapCoreLib 连自己写出来的这张图都再读不回来（`AssetPropertyCollection … 参数名: key`）。

对策：`REPAIR=1` 打开 `repair-roundtrip.py`，把被写坏的字节换回干净值（§6）。
官方例子图 / `hs_1v5_test` / `R3Training` 不需要 REPAIR（纯 round-trip 就干净）。

### 5.4 中文与文件名

地图里的字符串是 GBK。LCID 不是中文的 Windows 上写出来会变味。另外插件 readme 的硬约束仍然成立：
**导入的文件夹名 / lua 文件名不能是中文**，否则会损坏地图文件。
（注意：**传给注入器的组名参数**含中文时，命令行编码可能对不上 —— 用 `fields <map> #下标` 按
下标选组更稳。）

### 5.5 `_mix` 加密图

新地编"地图加密"产出的 `_mix` 图**编辑器都打不开**（官方不提供解密），`MapTool info` 会抛
`BadMapException: … corrupted or encryted`。用户 38 张图里 20 多张是 `_mix` → 这条路只覆盖
**未加密的图**。

### 5.6 尾部占位

脚本尾部会被 MapCoreLib 吞掉一部分字符（插件源码原话"可能是库的bug"），所以注入时在正文后补
`--redundancy`（默认 100）条 `-- end of script, please ignore this line`。本工具沿用。

---

## 6. `REPAIR=1` 修复流水线（§5.3）

```
clean.raw = MapTool resave 0 <源图>                ← Dreamness 写的、干净的（同一套布局）
mc.raw    = InjectMapScript roundtrip raw <源图>   ← MapCoreLib 纯 round-trip（无注入）
inj.raw   = 注入后的 <输出>.map.raw                ← 基准
repair-roundtrip.py：逐字节比对 clean.raw / mc.raw，把被写坏的偏移**映射到 inj.raw** 上复原；
                      PlayerScriptsList 区间跳过（那是我们注入的版本，不能覆盖）。
```

偏移映射：注入只在开头 stringPool 追加了名字（`pool_delta` 字节）、把 PlayerScriptsList 撑大了
（`psl_delta` 字节）—— PSL 之前 `+pool_delta`、PSL 之后 `+pool_delta+psl_delta`。这些量由脚本
自动算，PSL 的 `dataStartPos`/`dataSize` 由注入器打印（`PSL-OFFSETS` 行）。

---

## 7. 要造一张"带基地车起点"的测试图该怎么做

**路线 A（推荐，全自动）：克隆一张已经打过遭遇战脚本的现成图 + 深拷贝一个模板组。**

```bash
REPAIR=1 ./inject-map-script.sh add <有出生点的图.map> out/MyTest.map ../init.lua \
    <模板组名> <新组名> init.lua --template-map <有组能跑的图.map>
```

`R3Training.map` 里已经有 `Player_1_Start` / `Player_2_Start` 航点，`hs_1v5_test/1v5(v1.3).map`
是正规遭遇战图（8 玩家号位）—— "克隆 + 注入"两步就得到能测的图。装到游戏里：

```
%APPDATA%\Red Alert 3\Maps\<地图文件夹>\
    MyTest.map                     <- 本工具产出的
    MyTest\init.lua                <- 脚本源文件
    MyTest\.lib_meta.json
    map.xml / map_meta.json / overrides.xml   <- 从底板克隆过来
```

**路线 B（需要人工）：地编伴侣里新建。** `WbLauncher.exe` →「新建地图」→ 编辑器自动放
`Player_N_Start` 航点 → 打开「Lua导入工具」插件 → 导入文件夹 → 保存。

**路线 C（做不到）：加密图**（§5.5）。

---

## 8. 本次验收产物与证据

| 文件 | 说明 |
|---|---|
| `out/R3Training.inplace.map` (+`.raw`) | ① `replace` 原地替换（组 `Example_origin_05_top_button` / `init.lua`） |
| `out/hs_1v5_test.HSTest.map` (+`.raw`) | ② `add --template-map`：从 R3Training 原版图深拷贝组，注入到**自己没有任何脚本组**的 `1v5(v1.3)` 遭遇战图，组名 `HSTest` |
| `out/超爽防守.add.map` (+`.raw`) | ③ `add`：深拷贝超爽图自带的组（不碰它原有脚本），组名 `R3MapLib`；带 `REPAIR=1` |
| `out/VERIFY-INPLACE.txt` | 字段对照表 + 解析器校验 + 逐字节证据（原始输出） |

三份成品的关键指标（原始输出在 `VERIFY-INPLACE.txt`）：`Errored=False`、资产 26 个、**只有
`PlayerScriptsList` 变化**、脚本组数正确、未压缩副本里能按字节搜到探针的 `HS-TEST`。

`out/PIPELINE-EVIDENCE.txt` 是**上一轮（旧"新建脚本组"路线）**的证据，仅作历史对照。

---

## 9. 目录

```
tools/map-lib/inject-map-script/
├── InjectMapScript.cs        # x86 注入器源码（dump/fields/replace/add/roundtrip）
├── build.sh                  # 用 .NET Framework 的 csc 编译（x86）
├── MapTool/
│   ├── Program.cs            # net8：info / resave / grouplua / newmap / slinfo / reflect
│   ├── MapTool.csproj        # 引用地编伴侣的 Dreamness.RA3.Map.Parser.dll
│   └── build.sh
├── inject-map-script.sh      # 一条命令跑完整条流水线 + 自证（replace / add）
├── repair-roundtrip.py       # 修 MapCoreLib 往返写坏的资产字节（REPAIR=1）
├── dev/ReflectDump.cs        # 反射探针（查 MapCoreLib 内部类型/字段，调试用）
├── runbounded.sh             # 调试用：跑 N 秒后强杀（对付会挂住的解析）
├── bin/                      # InjectMapScript.exe + MapCoreLib.dll + Newtonsoft.Json.dll
├── MapTool/bin/Release/net8.0/
└── out/                      # 产物与证据
```

`tools/map-lib/init.lua`、`tools/map-lib/probe.lua` 全程**只读引用**，没有改动。
