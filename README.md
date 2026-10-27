# presents.QQSuite · QQ 全能套件

把 QQ 聊天、QQ 单向发送、QQ 群聊管家、QQ 群文件管家、QQ 工具箱、QQ 增强、QQ 空间、QQ 分享卡片、视频解析、Pixiv 图片、JM 漫画、表情包、跨群转发与群聊日报整合进一个插件，全部功能复用同一条 OneBot v11 连接；在「功能检视 → QQ 全能套件」里可逐个开关，也可一键全开。兼容 NapCat、SnowLuma、LLOneBot 与新旧版 QQ。

包含的子模块：

- `Alife.Function.QChat` —— QQ 聊天
- `QQSuite.ChatSender` —— QQ 单向发送（复用聊天连接，只发不收）
- `Alife.QQEnhance` —— 贴表情 / 点赞 / 撤回 / 禁言 / 戳一戳 / 音乐与 B站卡片 / 互动提示
- `Alife.Qzone` —— QQ 空间
- `Suran.GroupManager`、`Suran.GroupFileManager` —— 群聊与群文件管理
- `Suran.DailyReport` —— 群聊日报
- `Suran.ShareArk`、`Suran.VideoShare`、`Suran.PixivShare`、`Suran.JmShare`、`Suran.MemePack`、`Suran.QQForward` —— 分享卡片与媒体
- `YuYang.QQTools` —— QQ 工具箱

## 安装

解压后目录名必须与插件 id 一致（`presents.QQSuite`），放到 Alife 的 `Storage/Plugins/` 下，重启后由客户端自行编译加载。需要已连接的 OneBot v11 实现（NapCat / SnowLuma / LLOneBot）。

## 版本

当前版本 **5.2.1**，发布包见 [Releases](https://github.com/presents0721/presents.QQSuite/releases)。

插件大版本与 Alife 大版本对应：5.x 只给 Alife 5.0.0+ 用，4.x 请勿安装或更新到 5.x（两条线功能一致，底层接口按版本适配）。

## 源码

仓库结构即插件目录结构：`manifest.json` + `*.cs` + `Resources/`。运行时产生的 `out/`、`jm/`、`meme/`、`state.json`、`credentials.json` 等不进仓库（见 `.gitignore`）。
