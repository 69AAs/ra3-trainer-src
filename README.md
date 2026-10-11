# RA3 Trainer

RA3 Trainer 是一个面向《Command & Conquer: Red Alert 3》的 Windows x86 修改器项目。它的定位是对一个远古 RA3 修改器程序进行现代化移植，用现代 .NET 桌面程序重新整理旧程序的核心功能与发布方式，便于本地单机环境使用和维护。

当前仅支持 RA3 1.12.3444.25830，对应目标进程为 `ra3_1.12.game`。

## Fork 改动

本 Fork 在原仓库之上进行了以下改进：

### UI 现代化
- **军用 HUD 暗色主题**：深炭黑底 + 军绿分隔线 + 琥珀强调（`Themes/HudAmber.xaml` → `SemanticTokens.xaml` → `GlobalStyles.xaml` 三层），直角硬边、LED 状态灯、等宽字体，替换了原有的硬编码浅色风格；规范见 `.trellis/spec/app/palette-hud.md`。
- **按钮语义分层**：
  - **Primary**（Mauve 紫色实心）：核心操作按钮（检测进程、一键启动、执行队列、Feature 开关）
  - **Secondary**（暗色线框）：辅助操作按钮（浏览、保存、预设管理等）
  - **Danger**（hover 红色预警）：危险操作按钮（恢复 Patch、清空队列、移除队列项）
- **Feature 状态反馈**：Feature 开启后按钮变为绿色实心（SuccessBrush），直观反馈开关状态。
- **连接状态指示**：顶栏标题下方显示颜色圆点（红=未连接，绿=已连接，黄=处理中）。
- **布局重组**：顶栏精简为纯进程连接模块，新增独立的"基础资源"卡片（Money/Power/SC Point），增援预设按钮间距优化。
- **等宽字体**：内嵌 Sarasa Mono SC（更纱黑体），统一全界面终端风格字体。
- **浮窗模式**：顶栏可切到置顶小浮窗（详见「使用注意」）。
- **输入框层级**：输入框背景深于卡片背景（Base vs Surface0），形成可辨识的视觉凹陷。

### 技术架构
- WPF 主题文件位于 `src/Ra3Trainer.App/Themes/`（CatppuccinMocha → SemanticTokens → GlobalStyles 三层架构）
- 新增 `ConnectionStateToBrushConverter`（项目首个 IValueConverter）
- `MainViewModel` 新增 `ConnectionState` 枚举，`FeatureItemViewModel` 新增 `IsActive` 计算属性

## 重要声明

- 本项目仅建议用于本地单机、离线测试或存档研究。
- 本项目依然不适用于 PVP、线上对战、天梯、联机竞技或任何会影响其他玩家体验的场景。
- 本项目会读写目标游戏进程内存，可能被安全软件拦截或误报。
- 本项目尚未经过严格的安全审查和稳定性审查，可能存在崩溃、误写内存、兼容性异常或其他未发现的问题。
- 请谨慎使用，并自行承担使用风险。建议在使用前备份存档和相关配置。

## 功能概览

- 启动与附加：配置 `RA3.exe` 路径，启动游戏并等待真实 `ra3_1.12.game` 进程出现后附加。
- 版本校验：仅允许附加到匹配 RA3 1.12.3444.25830 的 32 位目标进程。
- 资源修改：支持金钱、电力、秘密协议点数等常见数值修改。
- 建造与技能：支持快速建造、秘密协议相关开关、超级武器冷却相关修改。
- 地图与视角：支持地图显示和视角缩放相关修改。
- 选中单位：支持弹药、生命值、速度、等级、摧毁、复制或创建选中单位等操作。
- 敌方限制：支持限制敌方建造、调整敌我伤害或无敌相关效果。
- 增援队列：支持按单位代码创建增援，并保存常用预设。
- 配置文件：程序启动后会在 exe 同目录查找 `Ra3Trainer.settings.json`；如果不存在，会自动释放默认配置文件。

## 构建要求

- Windows
- .NET 8 SDK
- 普通发布版运行端需要 .NET 8 Windows Desktop Runtime
- 自带运行库发布版不需要用户额外安装 .NET Runtime

## 验证

```powershell
dotnet test "Ra3Trainer.sln" -c Release --no-restore
dotnet list "Ra3Trainer.sln" package --vulnerable
```

## 发布

普通单文件版（需要目标电脑已安装 .NET 8 Windows Desktop Runtime）：

```powershell
./scripts/publish-framework-dependent.ps1
```

自带运行库单文件版：

```powershell
./scripts/publish-self-contained.ps1
```

仓库包含 GitHub Actions 发布流程。推送 `v*` 标签，或在 GitHub 手动运行 `Build and Release` workflow，会构建上述两个版本并上传为 Release 资产。

运行时 manifest 和 bootstrap 脚本已嵌入程序。发布包不需要携带 `analysis/` 目录。其余分析材料、构建产物、截图、本地 Codex 环境文件、原始 `Trainer.exe` 不应上传或随包分发。

## 使用注意

- 目标进程必须是 32 位 `ra3_1.12.game`。默认要求文件版本精确匹配 `1.12.3444.25830`（hook 地址由该版本推导）。
- 游戏主程序被补丁改过时，可勾选顶栏的「允许非标准版本」，接受任意 `1.12.x` 版本；连接提示会显示实际检测到的版本号并标注兼容模式。该开关保存在 `Ra3Trainer.settings.json` 的 `AllowNonStandardVersion`，默认关闭（行为与之前一致）。
- 启动器路径用于启动 `RA3.exe`；工具会等待真实游戏进程出现后再附加。
- 程序会在 exe 同目录读取 `Ra3Trainer.settings.json`；如果文件不存在，会自动写出默认配置。
- 顶栏「浮窗模式」打开小浮窗（默认折叠）：置顶、无任务栏图标、**永不抢游戏焦点**（`WS_EX_NOACTIVATE`），随处可拖，位置记在设置的 `FloatWindow` 段里。因为浮窗拿不到键盘焦点，它里面只有按钮，需要输入的资源值/启动参数仍在完整窗口里改。
- 主窗口「运行状态 / 取证」卡显示：目标进程/版本/位数/模块基址、hook 点校验结果、已启用功能数；并带「校验 Hook 点」「导出取证包」「打开日志目录」「打开取证目录」四个按钮。
- **会话日志**：`logs\session-<日期>.jsonl`（JSON 行，记录 detect/attach/hook 校验/每次功能动作/退出码，保留 14 天）。
- **取证包**：`crashdumps\evidence-<目标进程>-<时间戳>.zip`，内含会话日志、hook 点期望/实际字节、附加时的模块清单、系统 WER 崩溃报告、目标目录里最近改动过的日志、系统事件日志片段。
- 「装 patch 前校验 hook 点」默认开启：把每个 hook 点的现存字节与 patch 清单比对；**全部不符时拒绝安装**（避免往陌生版本的内存里写跳转），部分不符会告警后按你的要求继续。
- 「崩溃自动取证」默认开启：目标进程带着 patch 以非零退出码结束时，自动生成取证包。
- **单实例**：同时只允许一个实例（两个实例往同一批代码点写跳转会互相踩踏）。
- 以上诊断只依赖**目标进程名 + 附加时记录的模块路径 + patch 清单**，不依赖任何 mod 的名字或目录结构：换 mod 照样可用。
- 修改器会写入目标进程内存，可能触发杀软提示；建议仅在本机单机环境使用。
- 如果进程权限不足，尝试以管理员权限启动修改器。
