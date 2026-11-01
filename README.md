# presents.QQSuite · QQ 全能套件

把 QQ 聊天、QQ 单向发送、QQ 群聊管家、QQ 群文件管家、QQ 工具箱、QQ 增强、QQ 空间、QQ 分享卡片、视频解析、Pixiv 图片、JM 漫画、表情包、跨群转发与群聊日报整合进一个插件，共 14 个功能卡片，全部复用同一条 OneBot v11 连接；在「功能检视 → QQ 全能套件」里可逐个开关，也可一键全开。兼容 NapCat、SnowLuma、LLOneBot 与新旧版 QQ。

## 子模块

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

## 5.2.x 更新与优化

### 5.2.2

- 适配 Alife v5.2.0 的 `ChatStreamingAsync` 新签名：QQ 空间与群聊日报改用 `ChatResult`（异常需显式检查，不再从调用处抛出），修掉新版客户端上的编译/运行失败。
- 新增「语音转文字」：调用本机 whisper.cpp 把 QQ 语音转成文本再交给 AI。全程本地、音频不出网；语种默认自动检测（纯英文不会被强行当中文解码）；程序与模型路径在配置里自行填写，默认留空。
- 新增「动图抽帧拼图」：GIF 在本地均匀抽帧拼成一张网格图，消息里给出图片路径，AI 用 `LoadImage` 打开即可看到动作序列，解决多模态上游只保留第一帧的问题。静态图不受影响。
- 回复方式改为「直接说话 / 引用」：`QChat` 的函数描述与用法示例不再教 AI 用 `@`，改为提示用 `ReplyRecent` 引用对方——`@` 会打扰人。
- 「群聊附加提示词」新增触发概率，「互动提示」概率默认值下调：`0` = 从不注入，`100` = 每条都注入。文案本身保留，随时调回来；记忆插件已经记住这些规则时可以长期设为 0。
- 提示词与配置默认值不再写死任何本机绝对路径。

### 5.2.1

- 修复 NapCat 上「检索群历史消息」必然失败：`get_group_msg_history` 传 `message_seq=null` 会被判 schema 错误（retcode 1400），现在要最新消息时整个键都不发。
- 群文件下载失败提示改为指向「NapCat 未适配本机 QQ 版本」，不再误提示「需启用 packetBackend」。
- 实测 Bot 为普通成员（非管理员）也能检索群历史、列群文件、取下载直链并真实下载文件。

### 5.2.0

- 新增「QQ 单向发送」：复用 QQ 聊天的同一 OneBot 连接只发不收，提供 `QChatSend` / `QChatSendFile` / `QChatSendImage`（含表情库匹配）。
- 各模块注入提示词精简约 43%，删除与 QQ 聊天重复的 CQ 码/身份文档；表情库列表注入加 30 分类 / 60 独立表情上限。

## 依赖

manifest 声明：`FunctionCaller` / `Speech` / `MessageFilter` / `SystemEvent` / `AIModelUtility` / `Vision`。

## 隐私与数据

- 运行时产生的 `out/`、`jm/`、`meme/`、`state.json`、`credentials.json`、`*.log` 等不进仓库/发布包。
- 语音转文字与动图抽帧全部在本机完成：语音不经过任何第三方接口，动图只从 QQ 图床下载，处理完即删。
- 发布源码不含任何本机绝对路径、账号与 Token；whisper 的程序/模型路径由使用者自己在配置里填写。
