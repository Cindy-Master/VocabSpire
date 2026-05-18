# VocabSpire — 杀戮尖塔2 背单词 Mod

## 项目概述

在 Slay the Spire 2 中打出卡牌时弹出英语单词测验：
- **答对** → 卡牌效果正常执行
- **答错** → 能量照扣、卡牌正常进弃牌堆，但卡牌效果不触发（惩罚）

## 技术栈

- **框架**: Godot 4.x + .NET 9.0 (Godot.NET.Sdk/4.5.1)
- **补丁**: HarmonyLib (`0Harmony.dll`) — Prefix/Postfix/TargetMethods
- **游戏 DLL**: `sts2.dll`（反编译源码在 `sts2_decompiled/`）
- **语言**: C# with nullable + implicit usings

## 目录结构

```
VocabSpire/
├── Plugin.cs                    # 入口: ModInitializer, InputListener (F8 快捷键)
├── Patches/
│   └── PlayCardPatch.cs         # 核心: OnPlayWrapper 拦截 + OnPlay 跳过
├── Models/
│   ├── QuizQuestion.cs          # 题目模型 + QuizMode 枚举
│   ├── WordBank.cs              # 词库模型
│   └── WordEntry.cs             # 单词条目
├── Services/
│   ├── GameBridge.cs            # 游戏 API 桥接 (GetUIRoot, SetGamePaused)
│   ├── VocabManager.cs          # 词库管理 + 出题
│   ├── VocabConfig.cs           # 配置持久化 (JSON)
│   ├── QuizGenerator.cs         # 出题算法 (加权随机)
│   └── FileParser.cs            # JSON/CSV 词库解析
├── UI/
│   ├── QuizPanel.cs             # 答题弹窗 (暂停游戏, 键盘 ABCD/1234)
│   └── VocabSettingsPanel.cs    # 设置面板 (F8, 词库选择/导入/模式)
├── Resources/wordbanks/         # 打包进 DLL 的示例词库
├── VocabSpire.csproj
├── VocabSpire.json              # Mod 清单 (id/name/version)
└── Directory.Build.props        # STS2GamePath 配置
```

## 核心补丁机制

### PlayCardPatch — 拦截 OnPlayWrapper

游戏打牌流程: `PlayCardAction.ExecuteAction` → 扣能量 → 播动画 → `CardModel.OnPlayWrapper`

补丁在 `OnPlayWrapper` 处拦截:
1. Prefix 拦截首次调用，暂停游戏，弹出答题面板
2. 玩家作答后取消暂停，设 `_bypass=true` 再次调用 `OnPlayWrapper`
3. 答错时同时设 `SkipEffect=true`

### OnPlaySkipPatch — 跳过卡牌效果

- 使用 `TargetMethods` 动态发现所有 `CardModel` 子类中重写的 `OnPlay` 方法（约 548 个）
- 当 `SkipEffect=true` 时，Prefix 返回 `Task.CompletedTask` 跳过效果
- `OnPlayWrapper` 的其余生命周期（移入弃牌堆等）正常执行

## 构建与部署

```bash
# 构建
cd VocabSpire
dotnet build -c Release

# 构建产物
.godot/mono/temp/bin/Release/VocabSpire.dll

# 部署到游戏 mods 目录（注意：是游戏根目录下的 mods/，不是 data_sts2_windows_x86_64/mods/）
cp .godot/mono/temp/bin/Release/VocabSpire.dll  "D:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/VocabSpire/"
cp .godot/mono/temp/bin/Release/VocabSpire.pdb  "D:/SteamLibrary/steamapps/common/Slay the Spire 2/mods/VocabSpire/"
```

## 关键路径

| 用途 | 路径 |
|------|------|
| 游戏安装 | `D:\SteamLibrary\steamapps\common\Slay the Spire 2\` |
| 游戏 DLL | `...\data_sts2_windows_x86_64\sts2.dll` |
| **正确的 mods 目录** | `...\Slay the Spire 2\mods\VocabSpire\` |
| 错误的 mods 目录 | `...\data_sts2_windows_x86_64\mods\` (游戏不读这里) |
| 游戏日志 | `C:\Users\26064\AppData\Roaming\SlayTheSpire2\logs\godot.log` |
| 运行时配置 | `...\mods\VocabSpire\vocabspire_config.json` |
| 运行时词库 | `...\mods\VocabSpire\wordbanks\` |

## 踩坑记录

1. **部署目录**: 游戏从根目录 `mods/` 加载 mod，不是 `data_sts2_windows_x86_64/mods/`。日志可确认: `Found mod manifest file ...\mods\VocabSpire\VocabSpire.json`
2. **OnPlay 补丁**: `CardModel.OnPlay` 是 `protected virtual`，每张卡牌子类都重写了它。仅补丁基类无效——必须用 `TargetMethods` 遍历所有子类
3. **暂停时 UI 交互**: QuizPanel 和 VocabSettingsPanel 必须设 `ProcessMode = Always` 才能在暂停时响应输入
4. **UI 初始化时机**: 使用 `CallDeferred` 延迟到场景树就绪后再创建 UI 节点

## 反编译参考

`sts2_decompiled/` 下有完整反编译源码，关键类:
- `MegaCrit.Sts2.Core.Models/CardModel.cs` — `OnPlayWrapper`(~L1436), `OnPlay`(~L1289)
- `MegaCrit.Sts2.Core.GameActions/PlayCardAction.cs` — `ExecuteAction` 打牌流程
- `MegaCrit.Sts2.Core.Nodes.Combat/NCardPlayQueue.cs` — 卡牌打出队列视觉节点
- `MegaCrit.Sts2.Core.Commands/CardPileCmd.cs` — `AddDuringManualCardPlay` 卡牌移动动画
- `MegaCrit.Sts2.Core.Nodes.Cards/NCard.cs` — `FindOnTable` 通过 PileType 查找卡牌节点
