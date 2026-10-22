# 界面文案的翻译源

`LocData.cs`（14 语言 × 325 键）由本目录的文件生成，**不要手改那个 .cs**。

```
tools/i18n/
├── source.json            键清单 + 中文原文 + 英文原文（唯一事实源）
├── locales/<lang>.json    各语言译文（zh-t/ja/ko/es/pt/fr/de/pl/cs/ro/ru/vi）
├── gen-locdata.py         生成 SpectreReplay/Ui/LocData.cs
└── gen-locdata.ps1        同上，PowerShell 移植版（本机没有 Python 时用）
```

> 两个生成器**逻辑等价**，产物逐字节相同（UTF-8 BOM + LF）。
> 没有 Python 的那台机器上跑 `.ps1`，有 Python 时跑 `.py` 更省事。

## 改文案的流程

1. 改 `source.json` 里的 `zh` / `en`（新增键就在这里加，**顺序即生成顺序**）。
2. 在 `locales/*.json` 里给新键补上 12 种译文。
3. 跑生成器：

   ```
   python tools/i18n/gen-locdata.py
   ```

4. 跑闸门 `pwsh -NoProfile -File tools/verify.ps1`。

漏任何一种语言，生成器会**直接拒绝生成**并列出缺失的键——它不会写出半张表。

## 为什么是生成 C# 字面量，不是运行时读 JSON

读文件多一条**运行时**失败路径（资源名对不上 / 流读不出 / JSON 坏了），而它的
失败方式恰好是最糟的一种：表为空，玩家界面上看到的是 `record.autosave` 这种原始
键名，日志里还未必有线索。编译进代码的字面量不可能在运行时缺失——漏了就是编译
不过，在闸门里当场变红。

## 判据在守什么

- **各语言键集合必须完全一致**（`LocFallbackCheck` ①）。少一条，该语言下就静默
  显示英文——不露键名、不报错，只查「露没露键名」永远抓不到。
- **回退链逐级可达**（`LocFallbackCheck` ②③④）：当前语言 → 英文 → 中文 → 键名。
- **两套语言映射必须一致**（`LocLanguageCheck` ①）：`Loc.CodeByLanguage` 与
  `LocalizationManager.langlist` 分居两处，值不同时面板与通知会显示不同语言。
- **LocData 覆盖 Loc 声明的每一种语言**（②）：生成脚本漏掉一种语言时，
  `TryGetValue` 失败会静默回落英文。
- **桩件的枚举值必须与游戏真值一致**（③）：`tools/UiPreview/LocStubs.cs` 里的
  `ChineseSimplified = 40` 这类常量与游戏 `UnityEngine.SystemLanguage` 逐个比对。
  这条是**真抓过 bug 的**：曾有 `Italian = 16`（真值 21），离线套件自洽全绿，
  真机上却是选错语言。
