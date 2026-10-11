# 地图 Lua 导入 —— 机制查实 + 可行的无 GUI 路线（含原始输出）

> 本文件的所有结论都有下面的命令 + 原始输出支撑。全部操作只读用户目录
> （`C:\Users\user\AppData\Roaming\Red Alert 3\Maps\` 与 `D:\Games\`），产物一律落在仓库
> `D:\work\ra3-trainer-refine\tools\map-lib\inject-map-script\out\`。

---

## 一、脚本到底存在哪？（疑点结论）

**存在 `.map` 文件内部**，具体是 `PlayerScriptsList` 资产里一条 ScriptAction 的字符串参数，
格式 = `"#!ra3luabridge\r\n"` + **Lua 源码明文** + `"\r\n"` + 100 行 `-- end of script, please ignore this line`。
日冕的 LuaBridge 就是拿 `DEBUG_MESSAGE_BOX` 这个 action 的消息参数当 Lua 源码执行。

原始证据（把官方例子图解压后直接看字节）：

```
$ MapTool resave "…/Example_origin_05_top_button.map" out/example.raw.map 0
打开: Errored=False  源文件 33833 bytes
另存: out/example.raw.map  7697205 bytes  compress=False

$ dd if=out/example.raw.map bs=1 skip=7684250 count=1400 | tr -d '\000'
\u0003<\u0015\u0003)\u0001
\u001e\u0015#!ra3luabridge
Example05_meta = {
    things = {'AlliedCommandoTech1', 'AlliedFighterAircraft', 'CelestialAntiAirShip'},
    button_index = 2, Player_1 = 1, Player_2 = 1, unit_index = 1
}
exCenterTopBtnsChangeVisibility(1)
exCenterTopBtnShow(Example05_meta.button_index, 'Button_PlayerPower_ProductionKickback', '获得随机单位')
function onCenterTopBtnClickEvent(player_name, button_id) … end
-- end of script, please ignore this line-- end of script, please ignore this line…
```

而 `map.xml` 里那个 `<GameScriptList>` 是**空壳**——它的 action 参数是空的：

```xml
<Script id="init.lua"><Action Type="DEBUG_MESSAGE_BOX"><ParameterList/></Action></Script>
```

⇒ 官方图能跑，内容只可能来自 `.map`。**`map.xml` 只存脚本组的骨架，不存内容；
文件夹旁隐藏的 `.lib_meta.json` 只是导入插件读的输入清单**（例子图里它甚至指向作者机器上的
`H:\Program Files (x86)\Red Alert 3(Incomplete)\…`），游戏不读它。

### 「官方插件跑完后 .map 与例子图逐字节相同」的原因

那张 `R3Training` **不是被插件写过的图，是整目录复制官方例子得到的**：

```
$ sha256sum "…/R3Training/R3Training.map"                → 38f982779450f24665648492a17e368e8714940a8164aa1f6eec72be1e861d9f
$ sha256sum "…/Example_origin_05_top_button.map"         → 38f982779450f24665648492a17e368e8714940a8164aa1f6eec72be1e861d9f   ← 与上完全相同
$ diff <(cat 例子/map.xml) <(cat R3Training/map.xml)
33c33   id="Example_origin_05_top_button">  →  id="R3Training">
84,86c84,86  Description / DisplayName / FileName 三行
165c165 id="Example_origin_05_top_button">  →  id="R3Training">
```

`map.xml` 只差 id/显示名/FileName 三处（人手改的），`.map` 一字不动；而且 `map.xml` 里脚本组名仍是
`Example_origin_05_top_button`（**如果插件真跑过、组名会是目标库名**）。所以「逐字节相同」是因为
**根本没有发生导入**，不是「导入写到了别处」。用户当时看到的「获得一个随机单位」按钮，
就是官方例子 `.map` 里那段 Lua 自己带来的。

---

## 二、官方导入机制（两条腿，都必须 GUI）

* **地编伴侣**（`…/地编伴侣/current/Ra3MapUtils.exe`，net8 WPF）：提供 *Lua 导入工具*，
  给某张地图**登记**要导入的 lua 库（`ILuaImportService` / `DoImport` / `.lib_meta.json`）。
  UI 文案（从 `Ra3MapUtils.dll` 抽出）：首页「特色功能」→ **Lua脚本快速导入(针对日冕等mod)**；
  地图右键 → **Lua导入工具**（`ManageLuaCommand`）；设置里 **Lua导入 - 冗余字符因子**（默认 100）。
* **新地编**（`WbLauncher.exe` → `WorldBuilder_Mod_1.12.exe`）：微程序 `RA3MapUtil_LuaImporter`
  在编辑器进程内用 MapCoreLib 把 Lua 写进 `PlayerScriptsList`，然后你**保存地图**。
  编辑器**自己不含 Roslyn**，微程序是它**通过 IPC 交给地编伴侣编译执行**的——
  `wbCore.dll` 原文：`请先启动“地编伴侣”。` / `无法连接到地编伴侣。` /
  `微程序“…”执行失败。` / `Unable to connect to MapUtils.`

## 三、无 GUI 自动化的可能性（结论：官方机制做不到）

* `MAP_TASKS.md` + `WbLauncher.exe` 里的开关字符串只有两个地图任务：
  `--open-map`、`--export-overview`（另有 `--restart-map/--editor-pid/--parent/--lan=…` 等启动器内部开关）。
  **没有任何「跑插件 / 导入脚本 / 保存」的命令行任务。**
* 实测 `WbLauncher.exe --bogus` → 不退错、挂住不返回（我 180s 超时后查进程确认它拉起了
  `WbLauncher.Core.exe --bogus`）。CLI 不是可用的无头任务通道。
* `地编伴侣` 确实开了 HTTP 服务 `http://localhost:24880/api/`，但固定路由只有
  `/api/ping`、`/api/maps`、`/api/maps/create`（`HandleGetMaps` / `HandleCreateMap`），
  **没有导入/执行脚本的路由**。
* 编辑器微程序依赖地编伴侣进程 + 编辑器进程都在跑，两者都是 GUI。

## 四、可行的无 GUI 路线：**.map 内等长原地补丁**（本次实现并验证）

思路：脚本就是 `.map` 里一段明文。**只要新内容与原内容字节数完全相同**，就可以原地覆盖，
于是所有长度字段、名字池下标、嵌套资产字段**一个字节都不变**。

```
$ MapTool resave <官方例子.map> out/patch-base.raw 0            # 解压（7697205 B）
$ python patch-lua-inplace.py out/patch-base.raw out/patch-injected.raw out/inplace.lua
脚本区 span = [7684282, 7689697)  长度 5415
新内容长度 = 5415（等长，原地覆盖）
已写出 out/patch-injected.raw（7697205 字节，与输入等长）
$ MapTool resave out/patch-injected.raw out/Example05.hs-inplace.map 1     # 重新压缩
另存: out/Example05.hs-inplace.map  41959 bytes  compress=True
复读: Errored=False 资产=26 脚本组=1
```

结构校验（Dreamness 解析器）：

```
打开成功: Errored=False  文件=out/Example05.hs-inplace.map  (41959 bytes)
资产数=26   全部 parsed=True errored=False
  [PlayerScriptsList] PlayerScriptsList ver=1 dataSize=10995 parsed=True errored=False   ← 与官方图完全相同
脚本组数=1
  group[0] dataSize=10855                                                                  ← 与官方图完全相同
```

逐字节证明（解压后对比官方图）：

```
官方未压缩  = 7697205      补丁未压缩  = 7697205      等长? True
不同字节数  = 5045
不同字节区间 = [7684298, 7689696]
脚本区 span = [7684282, 7689697)
全部差异都落在脚本区 span 内? True
脚本区之前 逐字节相同 = True
脚本区之后 逐字节相同 = True
标记 HS-INPLACE-PATCH 命中 = 1      旧字符串 获得随机单位(UTF-8) 残留 = 0
```

### 为什么这条路比原来那条「MapCoreLib 独立进程写回」可靠

同一张官方图，用 **原注入器把「原封不动的 Lua」写回去**（等价于无损往返），输出**依然与源图不同**：

```
$ ./inject-map-script.sh replace <官方例子.map> out/roundtrip_orig.map out/orig_init.lua \
      Example_origin_05_top_button init.lua
   ~ PlayerScriptsList   源=(…, 10995, …)   输出=(…, 10998, …)      ← +3 字节
   资产总数=26  一致=25  变化=1
$ python (difflib 对齐 out/example.raw.map vs out/roundtrip_orig.map.raw)
== replace  …\xe8\x8e\xb7\xe5\xbe\x97…  (UTF-8 "获得随机单位")   ← 官方图里是 UTF-8
== replace  …\xbb\xf1\xb5\xc3…          (GBK  "获得随机单位")     ← MapCoreLib 写成了 GBK
== delete A[206:246]=40B / insert B[5652:5692]=40B              ← 嵌套脚本资产重排
```

即：**MapCoreLib 单独跑一边，即使输入=输出内容，也会重排嵌套资产（+3 字节）并把非 ASCII 改编码**。
脚本区是「嵌套资产图」，任何重排都可能让游戏整组跳过——这正是实测「脚本不生效」的机制来源。
等长原地补丁把变量压到只剩 Lua 正文本身。

另外实测：**MapCoreLib 独立进程连官方图都读不动**（缺编辑器宿主状态），90s 超时被强杀：

```
$ ./runbounded.sh 90 out/mc-dump.log ./bin/InjectMapScript.exe dump out/Example05.hs-inplace.map Example
[warn] initScriptSpec: 调用的目标发生了异常。
[runbounded] 超时 90s，强杀 (pid=841)
```

⇒ MapCoreLib 必须带着编辑器的 `initScriptSpec`/贴图表/字符串池等宿主状态才能干活，
这印证了「插件只能在编辑器进程内跑」。

---

## 五、若必须人工点：精确到菜单名

1. 打开 **地编伴侣**（`…/NewWorldBuilder/地编伴侣/current/Ra3MapUtils.exe`）。
2. 首页 → 「特色功能」→ **Lua脚本快速导入(针对日冕等mod)**；
   或在地图列表里**右键目标地图** → **Lua导入工具**。
3. 在该窗口里把 lua 库**文件夹**加进来（按钮：**包含到库中**／**导入**），
   脚本配置里可设 **每帧运行 / 只运行一次 / 评估间隔 / 生效难度**；
   注意提示 **「导入数据前, 请先关闭Ra3地编」**。
4. 打开 **新地编**（`WbLauncher.exe`，若尚未安装过插件，先在地编伴侣里点 **绑定新地编 / 指定新地编路径**）。
5. 打开目标地图 → 运行 **微程序**（`RA3MapUtil_LuaImporter`，「Lua导入工具」）→ **保存地图**
   （文件 → 保存 / Ctrl+S）。
6. **成功的标志**：保存后 `.map` 的 `PlayerScriptsList` 变大；`map.xml` 里多出一个
   `<GameScriptList id="_neutral_"><ScriptSet><ScriptGroup id="<库名>">`（action 的
   `<ParameterList/>` 仍是空的）；进游戏顶部出现按钮、消息区出现脚本消息。
   （注意文件夹名/lua 文件名**不能是中文**，插件 readme 原话。）

---

## 六、本次产物

| 路径 | 说明 |
|---|---|
| `out/Example05.hs-inplace.map` | **等长原地补丁后的成品 .map**（41959 B，Errored=False，PlayerScriptsList=10995 与官方一致） |
| `out/Example05.hs-inplace.map.raw` | 其解压副本（7697205 B，用于逐字节取证） |
| `out/install_hs_luatest/` | 可直接丢进 `%APPDATA%\Red Alert 3\Maps\` 的整套地图文件夹（.map/.tga/map.xml/map_meta.json/overrides.xml/map.str + 库文件夹） |
| `out/patch-base.raw` / `out/patch-injected.raw` | 补丁前/后的解压字节 |
| `out/inplace.lua` | 本次写入的 Lua（纯 ASCII，含标记 `HS-INPLACE-PATCH`） |
| `out/roundtrip_orig.map*` | 无损往返反例（+3 字节、编码漂移） |
| `patch-lua-inplace.py`（本次新建） | 等长原地补丁工具 |
| `make-install-folder.py`（本次新建） | 生成可安装的地图文件夹 |
| `out/mc-dump.log` | MapCoreLib 独立进程读官方派生图挂死的原始日志 |

**未做**：进游戏实机验证（红警3 正在用户会话里运行，且纪律禁止启动游戏/驱动 GUI）。
结构层面已证明该 `.map` 与官方可运行图的差异**仅落在 Lua 正文字节内**。
