# VocabSpire — 杀戮尖塔2 背单词 Mod

在 Slay the Spire 2 中打出卡牌时弹出英语单词测验，答对继续出牌，答错扣费但不执行效果。

## 功能

- **四种答题模式**：英→中选择、中→英选择、拼写、听力（在线TTS发音）
- **多选题**：多义词自动拆分为多选题
- **难度递增**：按游戏层数(Act)递增难度，支持分层独立配置
- **词汇图鉴**：注入游戏原生百科大全，展示解锁进度/掌握/错题统计
- **篝火复习**：休息点自动弹出错题复习，支持拼写/选择模式
- **全局回顾**：结算页词汇回顾按钮，翻页/筛选/导出错题本(CSV/JSON)
- **历史绑定**：每局回顾与游戏历史记录强绑定，可复盘
- **联机适配**：单人/联机自动切换拦截策略，网络同步答题结果
- **游戏原生UI**：复用游戏字体(Noto Sans CJK)、配色(StsColors)、图标、音效、动画
- **自定义词库**：支持 JSON/CSV 格式导入，可多词库切换

## 下载

- **GitHub Releases**：[最新版本](https://github.com/Cindy-Master/VocabSpire/releases/latest)
- **夸克网盘**：https://pan.quark.cn/s/d29f52ae5be8
- **QQ 交流群**：`750809524`（反馈 bug / 索取词库 / 玩法讨论）

## 安装

1. 下载最新版 `VocabSpire-vX.Y.Z.zip`
2. 解压到游戏 `mods/` 目录：
   - Windows: `Steam\steamapps\common\Slay the Spire 2\mods\VocabSpire\`
   - macOS: `SlayTheSpire2.app/Contents/MacOS/mods/VocabSpire/`
3. 启动游戏，选择"加载 Mods"

## 快捷键

- **F8**（可自定义）：打开设置面板

## 自定义词库

将 `.json` 或 `.csv` 文件放入 `mods/VocabSpire/wordbanks/` 目录，在设置面板中切换。

JSON 格式：
```json
{
  "name": "我的词库",
  "words": [
    { "english": "apple", "chinese": "n. 苹果", "phonetic": "/ˈæp.əl/" },
    { "english": "run", "chinese": ["v. 跑步", "vi. 运转"], "phonetic": "/rʌn/" }
  ]
}
```

## 构建

需要 .NET 9.0 SDK + Godot.NET.Sdk 4.5.1。

1. 修改 `Directory.Build.props` 中的 `STS2GamePath` 为你的游戏安装路径
2. `cd VocabSpire && dotnet build -c Release`
3. 产物在 `.godot/mono/temp/bin/Release/VocabSpire.dll`

## 许可证

MIT License
