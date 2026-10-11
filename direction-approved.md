# 方向确认（Gate 文件 · huashu-design 三方向硬门）

日期：2026-10-06 16:51
项目：RA3 修改器前端重做（游戏内注入面板 + 主窗）

## 三方向初稿（真实视觉，已渲染给用户看）
| 方向 | 逻辑来源 | 初稿 | 截图 |
|---|---|---|---|
| A · 巨型字大字报 | 秒数轮盘 #6（Bloomberg Businessweek / Richard Turley 谱系） | `_ui_directions/A-巨字大字报.html` | `mock/render/dir-A-巨字大字报.png` |
| B · 座舱仪表 | 现实参照（Elite Dangerous 座舱 UI） | `_ui_directions/B-座舱仪表.html` | `mock/render/dir-B-座舱仪表.png` |
| C · 网格制 | 最佳设计师（Vignelli · 纽约地铁标识体系） | `_ui_directions/C-网格制.html` | `mock/render/dir-C-网格制.png` |

## 用户选择
**用户原话：「C」** —— 选定 **C · 网格制（Vignelli）**。

## 选定的视觉条款（实现必须遵守）
- 表面：`#111` 底 / `#222` 行分割 / `#333` 结构线；文字 白 / `#888` 次要 / `#666` 标签
- 唯一强调色：**正红 `#D0021B`**（不用琥珀、不用蓝）
- 排版：严格四列网格（LED 14px / 功能 1fr / 值 auto / 命令 auto）；大写小字距列头 `功能 值 命令`
- 层级：靠**网格对齐 + 字号 + 反色**，不靠卡片/圆角/阴影
- 当前行 = **白底黑字反色** + 红 `执行`（不是染色底）
- 页签 = 顶部四等分网格，当前项红底
- 字体：正文 `Segoe UI Variable Text, Segoe UI`；数字与元信息 `Consolas`
