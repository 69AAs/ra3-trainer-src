# 红警3修改器（RA3 Trainer）源码

《命令与征服：红色警戒3》单机辅助工具的**完整源码、测试与构建脚本**。

- 🎮 **下载成品（面向玩家）**：https://github.com/69AAs/ra3-trainer
- 构建：`dotnet publish src/Ra3Trainer.App/Ra3Trainer.App.csproj -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true`
- 测试：1429 个单元测试 + 面板渲染夹具（515 项检查）
- 环境：Windows + .NET 8 SDK（游戏本体不在本仓库内）

## 许可

允许阅读、学习、修改、自行编译、个人非商业使用；**禁止任何商业用途**（打包售卖、收费分发、预装收费等）；
再发布衍生版本必须保留本许可、注明来源，并以同样的非商业条件发布。详见 [LICENSE](LICENSE)。

游戏名称、商标与相关素材归其各自权利人所有；本项目与游戏开发商、发行商无任何关联。请勿在联机对战中使用。
