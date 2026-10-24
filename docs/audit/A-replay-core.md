# 回放编解码与数据层（Codec / Data）审计报告

范围：`SpectreReplay\Features\Replay\Codec\`（ReplayCompression / ReplayCrypto / ReplayDecoder / ReplayEncoder）
＋ `SpectreReplay\Features\Replay\Data\`（Replay / ReplayData / ReplayConstants / ReplayKeys / ReplayFileInfo /
CursorSample / HitSnapshot / keyEvent）共 12 个文件，全部逐行读过。
涉及「游戏侧数据形状」的判断均在 `D:\Projects\adofai-libs\source\Assembly-CSharp\`（`Releases.cs:3`
`releaseNumber = 150`）核实过。

## 结论摘要

**无 P0。** 最严重的一条是 P1：`ReadCount` 的「每条记录最小字节数」把 v2 才有的 `HitTick` 标记字节
算进了最小值，于是**所有 `HitContextVersion = 1` 的旧回放（r149 及更早、含上游 Spectre 存量文件）
100% 加载失败**，而解码器里专门为 v1 写的那段分支成了永远走不到的死路。
其次是 P2 的资源放大：512MB 解压上限 × 「count 与剩余字节同量级」的守卫，让一个几百 KB 的
`.psprp`（不加密、无 MAC，任何人可造）能换出约 2GB 的托管分配（加载卡死/OOM）。
顺带记一条口径：注释里「旧构建读新文件…双向兼容」只对**已经带压缩包装层的构建**成立
（包装层是本地引入的，上游 Spectre 没有），详见「版本适配观察」。同样，解码器里为 v1
写的那段分支目前是死路（就是 P1 那条）。
其余 P3 集中在「只写不填的死字段（StartTime/SpVersion）」「列表所有权不清（2 处共享引用 + 1 处深拷贝，
`Reset()` 会清掉另一侧）」「PBKDF2 迭代上限 1e6 让点开一份手改文件在主线程卡 0.6~2 秒」「ReplayKeys
33 个常量零消费者」这类结构性/文档漂移问题。
加密本身（随机 salt/IV、双密钥派生、encrypt-then-MAC、先验 MAC 后解密、定长时间比较）**没有发现问题**，
容器各段读写逐字段对称，段版本上界四重校验齐备。

## 发现

### F1 [P1] `ReadCount` 的最小记录长度把 v2 才有的 `HitTick` 标记算进去，所有 v1 旧回放 100% 被误杀

- 位置：`SpectreReplay/Features/Replay/Codec/ReplayDecoder.cs:54`（`MinHitSnapshotBytes`）、
  `:60-75`（`ReadCount`）、`:176-199`（读取与 `ver >= 2` 分支）；
  对照 `SpectreReplay/Features/Replay/Data/ReplayConstants.cs:19-22`、
  `SpectreReplay/Features/Replay/Codec/ReplayDecoder.cs:168-175`（注释：显式上界是为了防未来版本静默错位）
- 证据：

  ```csharp
  // ReplayDecoder.cs:53-55
  private const int MinKeyEventBytes = 8 + 2 + 1;      // double + ushort + bool
  private const int MinHitSnapshotBytes = 4 + 8 + 4 + 1 + 1 + 1 + 8 + 8 + 1 + 1 + 4 + 1; // 含末尾 HitTick 最小 1B
  private const int MinCursorSampleBytes = 8 + 4 + 4;  // double + float + float

  // ReplayDecoder.cs:69-74
  long remaining = r.BaseStream.Length - r.BaseStream.Position;
  long maxByStream = remaining / minRecordBytes;
  if (raw > maxByStream)
      throw new InvalidDataException(
          $"Record count {raw} exceeds remaining stream ({remaining} bytes)");
  ```

  `MinHitSnapshotBytes = 42` 假设每条记录至少带 1 字节 `HitTick.HasValue` 标记（v2 布局）。
  但 v1 记录没有这一字节，实为 41 字节。v1 文件的 `remaining` 精确等于 `41 × count`
  （上游 r149 及更早的编码器在 hit contexts 之后就结束负载，没有尾部段——已用
  `git show '94d4eed^:Spectre/Features/Replay/ReplayEncoder.cs'` 与 r150 版本对比确认：
  **两者只差那 4 行 `HitTick` 写入**，metadata / key events 布局逐字段相同，
  `MetadataVersion` 两代都是 2）：

  `maxByStream = floor(41N / 42) = N - ceil(N/42) < N`，对任意 `N ≥ 1` 恒成立 ⇒ 必抛。
  例：N = 1 时 `maxByStream = 0`；N = 100 时 `maxByStream = 97 < 100`。
  也就是说 `ReplayDecoder.cs:194-198` 那段 `if (ver >= 2) { ... read HitTick ... }` 的
  v1 分支，在「有命中记录」的旧文件上**永远不可达**；`ReplayConstants.cs:21` 注释
  「v1 旧谱 HitTick 保持 null」是错的口径。

- 触发条件：加载任何 `HitSnapshotVersion = 1` 的合法旧回放（r150 之前录制，
  上游 Spectre 与本项目共用同一 `Magic`/口令/后缀，设计上互通）。`count = 0` 的旧文件才幸免。
- 影响：加载直接失败，`ReplayManager.ReadReplayData` 捕获后打印
  `回放解码失败: ... 文件头不匹配或数据损坏`（`ReplayManager.cs:156-163`），
  列表详情页显示 `decode_failed`（`ReplayFileInfo.cs:112-116`）。**报错但错因完全误导**——
  玩家的旧回放文件是完好的，却被判成「损坏」。属于静默的数据可达性丢失（还好有日志）。
  另外 `tools/UiPreview/ReplayCheck.cs` 的往返用例全部用当前版本编码（`Sample()` 恒写 v2），
  这条兼容路径**从来没有被任何断言覆盖**。
- 建议：按段版本选最小值——
  `int minBytes = ver >= 2 ? MinHitSnapshotBytes : MinHitSnapshotBytes - 1;` 再传给 `ReadCount`
  （等效于把它算成 41）。更稳的写法是把 `ReadCount` 改成「先读 count，再用
  `MinBytesPerRecord(ver)` 收敛」，避免常量与版本分支再次脱钩。
  建议同时给 `ReplayCheck` 加一条夹具：把当前编码结果的 `MagicHitSnapshot` 之后版本号改成 1
  并剥掉每条的标记字节，断言能解出 `HitTick == null` 且条数不变（现在这条路径零覆盖）。
  游戏侧依据：`scrPlayer.Hit(long? hitTick, bool isAuto)` 是 r150 才出现的
  （`Assembly-CSharp/scrPlayer.cs:734`），所以 r149 之前录出来的文件必然没有 tick。
- 置信度：高（字节算术与上游两代编码器的 diff 均已实测）

### F2 [P2] 512MB 解压上限 + 「count 与剩余字节同量级」的守卫 ⇒ 单个 `.psprp` 可换出约 2GB 托管分配

- 位置：`SpectreReplay/Features/Replay/Codec/ReplayCompression.cs:9`、`:119-135`；
  `SpectreReplay/Features/Replay/Codec/ReplayDecoder.cs:60-75`、`:150`、`:177`、`:222`
- 证据：

  ```csharp
  // ReplayCompression.cs:9, 126-132
  private const long MaxDecompressed = 512L * 1024 * 1024;
  while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
  {
      if (output.Length + read > MaxDecompressed) throw new InvalidDataException(...);
      output.Write(buffer, 0, read);
  }

  // ReplayDecoder.cs:69-74 —— 守卫只保证 count 与「剩余字节」成比例
  long remaining = r.BaseStream.Length - r.BaseStream.Position;
  long maxByStream = remaining / minRecordBytes;   // 11 / 42 / 16
  ...
  var events = new List<KeyEvent>(count);          // ReplayDecoder.cs:150，立即分配 count×8B 数组
  ```

  触发链：`.psprp` 不加密、无 MAC（`ReplayManager.cs:124-128` 按后缀直接 `no_encryption = true`），
  任何人可造。把 512MB 全零用 Brotli 压成几百 KB，套上新压缩头（`[0x02][512MB][brotli 体]`），
  负载写成 `合法段魔数 + ver + count` 再跟 `count×11` 字节零——`count` 取
  `remaining / 11`，守卫**必然放行**（它只验比例，不验绝对量），随后：
  - KeyEvents：`512MB/11 ≈ 4 880 万` 条 ⇒ `List` 背板 `8B×4880万 ≈ 390MB`，
    外加 4880 万个 `KeyEvent` 对象（x64 量级 ~32B）≈ 1.5GB；
    `Decode` 返回后 `ToReplayData`（`ReplayDecoder.cs:336-342`）还会**再深拷一份** ⇒ 峰值 ~3.8GB。
  - CursorTrack：`512MB/16 ≈ 3 355 万` 条 ⇒ 背板 268MB + 3.3 千万个 `CursorSample` ≈ 1GB。
  - HitSnapshots：`512MB/42 ≈ 1 278 万` 条 ⇒ 背板 102MB + 对象（4×double + 2×float + 3×int + 5×bool
    + `long?`，~64B）≈ 780MB。

- 触发条件：把上面这种 `.psprp` 放进回放目录并加载/生成视频。现有闸门
  `ReplayCheck⑸` 与 `CursorTrackCheck⑸` 只测 `count = 0x7FFFFFFF`（被比例守卫拒），
  **测不到「count 恰为 remaining/11」这种合法比例的巨大分配**，所以这不是重复报闸门已覆盖的行为。
- 影响：加载时主线程长时间 GC 停顿（数秒~数十秒），或直接 `OutOfMemoryException`
  （在 Unity/IL2CPP 下 OOM 常常表现为进程被杀）。属于「一份文件就能让游戏失去响应」的可用性问题，
  不需要任何密钥。
- 建议：三层同时收：
  ① `MaxDecompressed` 从 512MB 收到 64MB（真实回放是 MB 级：几十万事件也就十几 MB；
     512MB 是当初上游的拍脑袋值，注释 `ReplayCompression.cs:76-82` 自己也承认它不该被用来预分配）；
  ② 给 count 加**绝对值**上界（例如 200 万条），因为比例守卫本质上只能防「明显荒谬」；
  ③ `new List<T>(count)` 的容量改成 `Math.Min(count, 1 << 20)`（`List` 本来就会自己增长，
     预分配只是为了少几次扩容，不该为它承担 OOM 风险）。
- 置信度：高（比例守卫的行为可直接从代码推出；放大系数按 x64 对象头估算，量级不受影响）

### F3 [P3] PBKDF2 迭代上限 1e6：点开一份手改 `.sprp` 会在主线程卡 0.6~2 秒

- 位置：`SpectreReplay/Features/Replay/Codec/ReplayCrypto.cs:35-39`、`:171-177`
- 证据：

  ```csharp
  // ReplayCrypto.cs:35-39
  // 迭代次数的合法区间。**上限必须有**：文件头来自磁盘，可以是任意值；
  // 没有上限时一份手改过的文件写 iterations=2^31 就能把游戏卡死几十秒
  private const int MinIterations = 1000;
  private const int MaxIterations = 1000000;

  // ReplayCrypto.cs:170-177
  iterations = ReadInt32(data, p); p += 4;
  if (iterations < MinIterations || iterations > MaxIterations) { ...return null; }
  ```

  KDF 在 MAC 校验之前（必然如此，MAC 密钥本身来自 salt+iterations），所以**一份 MAC 校验必然
  失败的文件也能先让你付满这次 KDF 的代价**。实测本机 PBKDF2-SHA256：
  1e4 = 28ms、6e4 = 38ms、**1e6 = 625ms**（Unity Mono 通常更慢）。
  而写侧自己只用 `IterationsV3 = 10000`（`ReplayCrypto.cs:33`）。
- 触发条件：构造 v3 头把 iterations 写成 1000000 的 `.sprp`，玩家在回放列表点开详情
  （`Pages/ReplayInfoPanel.cs:86` → `ReplayFileInfo.FromFile` → `ReplayManager.ReadReplayData`
  → `ReplayCrypto.Decrypt`，整条链在 `OnGUI` 里同步执行）。
- 影响：单次点击卡顿 0.6~2 秒（不崩溃，但会被玩家当成「面板卡死」）。注释声称的
  「防主线程卡死」只做到了「有界」，量级仍偏大。
- 建议：把 `MaxIterations` 收到与写侧同量级（例如 100000，仍给未来 10× 余量）；
  或改成「发现 MAC 失败且 iterations > 10000 时，记一条日志并在 UI 上降级显示」，
  避免把 KDF 放在 UI 线程的成本直接变成交互成本。
- 置信度：高（数字为实测；具体卡顿时长取决于 CPU/运行时）

### F4 [P3] `StartTime` / `SpVersion` 是「只读不写」的死字段：编码永远写空，元数据持久化承诺不成立

- 位置：`SpectreReplay/Features/Replay/Data/Replay.cs:60-61`；
  `SpectreReplay/Features/Replay/Codec/ReplayEncoder.cs:65-66`；
  `SpectreReplay/Features/Replay/Codec/ReplayDecoder.cs:126-128`；
  对照 `SpectreReplay/Features/Replay/Data/ReplayFileInfo.cs:19-23`
- 证据：

  ```csharp
  // Replay.cs:60-61
  public DateTimeOffset? StartTime;
  public string SpVersion = "";

  // ReplayEncoder.cs:65-66
  w.Write(m.StartTime?.ToUnixTimeMilliseconds() ?? long.MinValue);
  WriteStr(w, m.SpVersion);
  ```

  全仓搜 `StartTime\s*=` / `SpVersion\s*=`：**只有** `ReplayDecoder.cs:127-128` 从文件里读出来赋值，
  没有任何生产者写它们。而保存路径是
  `SaveReplayNew → FromReplayData(replayData)`（`ReplayManager.cs:61`），
  `ReplayData` 里根本没有这两个属性 ⇒ 再存一次必然写回 `long.MinValue` / `""`。
  `ReplayFileInfo.cs:20-23` 已经承认「`StartTime` 在 `Replay` 里有字段、编码器也写了，
  但录制侧从来不给它赋值 ⇒ 读出来恒为空」，但 `SpVersion` 连这句说明都没有。
- 触发条件：任何一次保存。
- 影响：文件里这两个字段恒空；将来若有人按「录制时间」「上游版本号」做统计/兼容判断，
  会静默拿到空值，且没有任何断言能发现。属于结构性死面 + 注释与实现漂移。
- 建议：二选一——① 删掉这两个字段（连同 encoder/decoder 的写入与读取；因为
  `MetadataVersion = 2` 且布局不能随便动，删除要配一次段版本升位）；② 认领它们：
  起录时 `data.StartTime = DateTimeOffset.UtcNow`（并在 `ReplayData` 上加对应字段，让
  `FromReplayData` 不再丢值），`SpVersion` 由 `Main` 的 `ModEntry.Version` 填。
- 置信度：高（全仓赋值点已穷举）

### F5 [P3] `ToReplayData` / `FromReplayData` 三个列表的拷贝语义不一致（2 处共享引用 + 1 处深拷贝），`Reset()` 会清掉另一侧

- 位置：`SpectreReplay/Features/Replay/Codec/ReplayDecoder.cs:280-288`（FromReplayData）、
  `:336-345`（ToReplayData）；`SpectreReplay/Features/Replay/Data/ReplayData.cs:167-169`（`Reset`）
- 证据：

  ```csharp
  // ReplayDecoder.cs:280-288 —— KeyEvents 深拷贝，另外两个直接共享引用
  replay.KeyEvents = replayData.KeyEvents.ConvertAll(k => new KeyEvent { ... });
  replay.HitSnapshots = replayData.HitSnapshots;
  replay.CursorTrack = replayData.CursorTrack;

  // ReplayData.cs:167-169 —— Reset 直接清空这三个列表（引用是共享的，清的是同一份）
  KeyEvents.Clear();
  HitSnapshots.Clear();
  CursorTrack.Clear();
  ```

- 触发条件：任何同时持有 `Replay` 与 `ReplayData` 的代码。当前调用点
  （`ReplayManager.cs:61`、`:164`）都是「转手即弃」，所以现在不炸；但一旦有人缓存两者
  （例如给 UI 留一份 `Replay` 备份、或录制时把 `Replay` 交给视频渲染线程），
  一侧的 `Clear()` 会让另一侧空掉，一侧的 `Add()` 会让另一侧在 `foreach` 中途
  `InvalidOperationException`。
- 影响：潜在缺陷，现状未触发；但「一半深拷贝一半共享」没有任何注释解释，改一碰就会碎。
- 建议：两个方向都深拷贝（`HitSnapshot`/`CursorSample` 全是值类型字段 + `long?`，
  `new HitSnapshot { ... }` 逐字段即可，成本与 KeyEvents 同级）；或者显式把这两个属性
  标注为「所有权转移」，拷贝后把源列表置 null 并禁止写入。
- 置信度：高（代码事实）；影响面取决于将来的调用方式

### F6 [P3] 光标轨迹的时间单调性：写入不保证、解码不校验、消费端二分却依赖它

- 位置：`SpectreReplay/Features/Replay/Data/CursorSample.cs:1-16`（契约只在注释里）、
  `SpectreReplay/Features/Replay/Codec/ReplayDecoder.cs:221-229`（不校验）、
  消费端 `SpectreReplay/Features/Replay/Runtime/CursorTrackSampler.cs:36-43`
- 证据：

  ```csharp
  // CursorTrackSampler.cs:36-43 —— 二分隐含要求按 SongPosition 升序
  // 不变量：track[lo].SongPosition <= songPosition < track[hi].SongPosition
  int lo = 0, hi = last;
  while (hi - lo > 1)
  {
      int mid = (lo + hi) / 2;
      if (track[mid].SongPosition <= songPosition) lo = mid;
      else hi = mid;
  }
  ```

  解码器（`ReplayDecoder.cs:221-229`）只是按 count 逐条读，没有任何单调性检查；
  编码器（`ReplayEncoder.cs:112-123`）也不校验。契约只写在 `CursorTrackCheck` 的
  样例注释「含负 songposition 与单调推进的典型轨迹」里，代码里没有它的位置。
- 触发条件：手改/拼接的 `.psprp`（不加密，用户可直接编辑——这是格式的可移植性设计，
  不是异常路径）。正常录制路径目前是单调的：`RecordCursor` 每帧追加
  （`ReplayRecorder.cs:171-195`），而重开/换关会走
  `Patch_Restart → StopAll → StopRecording`（`Patch_Lifecycle.cs:37-41`、
  `SpectreState.cs:674-686`），所以播放中回退不会发生。
- 影响：样本乱序时二分落进错误的区间，插值出**错误的指针位置**——画面上指针瞬移/乱飘，
  没有任何报错，只有成片里看得出来。
- 建议：解码时线性扫一遍校验（顺手丢弃逆序样本，成本 O(n)），或让 `Sample` 在发现
  `track[lo].SongPosition > track[hi].SongPosition` 时退化为线性回退。
  顺带把「必须升序」写进 `CursorSample` 的类型注释。
- 置信度：中（代码路径确定；「手改文件」是否为实际用户行为需产品判断）

### F7 [P3] `ReplayKeys` 33 个常量 + 11 处 `using static` 全是死引用，注释仍在描述已不存在的字典格式

- 位置：`SpectreReplay/Features/Replay/Data/ReplayKeys.cs:1-47`；
  消费者名录：`ReplayDecoder.cs:3`、`ReplayFileInfo.cs:5`（`using K = ...` 别名也未使用）、
  `Patch_Lifecycle.cs:14`、`Patch_Playback.cs:11`、`Patch_Recording.cs:12`、
  `ReplayAnalyzer.cs:5`、`ReplayManager.cs:6`、`ReplayRecorder.cs:6`、
  `VideoRender.cs:5`、`VideoRenderJob.cs:10`、`Pages.cs:13`、`SpectreState.cs:11`
- 证据：逐个常量查 `ReplayKeys.<名>` 与裸标识符均为 0 引用（`strings[...]`/`ints[...]`/
  `doubles[...]` 这类旧字典访问在全仓已不存在）；`ReplayFileInfo.cs:5` 的 `using K = ...`
  别名也没有任何 `K.` 使用（大小写敏感核对过）。文件里每个常量都带着
  「原键 xxx」的注释，描述的是一个**已经被『去字典化』删掉的机制**：

  ```csharp
  // ReplayKeys.cs:8-11
  public const string FileName = "file_name";
  public const string KeybdSoundFileName = "keybdsound_file_name";
  public const string KeybdSoundHash = "keybdsoundHash";
  public const string SongName = "song_name";
  ```

- 影响：不产生运行时错误，但它是「读了注释以为还有一层旧格式兼容」的来源——
  实际不存在任何按键名取值的代码，所以「旧键名兼容」这件事在本项目里是**不成立的**
  （真正的旧格式兼容是上游的 `LegacyReplayData` + `ConvertFromOldFormat`，
  移植时（`f96aa86`）搬进来、随 `62266ba`「回放数据去字典化」整体删除：
  `git log --all -S LegacyReplayData` 可复核）。
  另外 11 处 `using static` 让每个文件的可用标识符集合变大，容易让「`JudgeMode`
  到底是常量还是属性」这种误读发生。
- 建议：整套删除（连同 11 处 `using static` 与 `ReplayFileInfo` 的别名）；
  若确实要保留给外部脚本当契约，就在类注释里写清「消费者是谁、在哪个脚本里」，
  否则它只是一份自称有兼容能力的死表。
- 置信度：高（引用数已穷举；`tools/deadcode-audit.ps1` 只查静态方法，查不到常量，
  所以这条不是重复报闸门）

### F8 [P3] 加密失败用 `null` 表示，调用方直接 `.Length` → NRE，日志被两条矛盾消息淹没

- 位置：`SpectreReplay/Features/Replay/Codec/ReplayCrypto.cs:108-112`；
  消费方 `SpectreReplay/Features/Replay/Runtime/ReplayManager.cs:69-77`
- 证据：

  ```csharp
  // ReplayCrypto.cs:108-112 —— 失败返回 null
  catch (Exception ex)
  {
      Debug.Log("Encrypt Error: " + ex.Message);
      return null;
  }

  // ReplayManager.cs:69-77 —— 不判 null 直接解引用
  if (!no_encryption) data = ReplayCrypto.Encrypt(data);
  using (FileStream fs = new FileStream(filePath, FileMode.Create, ...)) { opened = true; fs.Write(data, 0, data.Length); }
  ```

- 触发条件：`Encrypt` 内部任意异常（加密提供者不可用、内存不足等）。
- 影响：文件虽然会被 catch 分支删掉、`SaveReplay` 返回 false（不会留下半成品），
  但日志里先是一条 `Encrypt Error: ...`，再被
  `SaveReplay error: System.NullReferenceException` 顶掉——真因被第二层噪音掩盖。
- 建议：`Encrypt` 改成抛异常或 `TryEncrypt(..., out byte[])`；调用方在
  `data == null` 时直接 `return false` 并给一句准确文案。
- 置信度：高

## 已核查但判定无问题（避免下轮重复劳动）

- `ReplayCompression.cs:37-84` 算法号分派：先判旧布局 `0x1F` 再判算法号，且长度校验
  （`:54`）在 `ReadInt32`（`:57`）之前 —— 顺序正确，短文件不会越界。
- `ReplayCompression.cs:76-83` 预分配提示 `hint` 与压缩比挂钩，`expected` 已按 512MB 上界校验 ——
  注释与实现一致（`MemoryStream(capacity)` 不再被畸形头白拿 512MB）。
- `ReplayCompression.cs:119-135` 边读边限量，`output.Length + read` 用 `long` —— 无 int 溢出。
- `ReadCount`（`ReplayDecoder.cs:60-75`）负数拒绝、0 允许、`long` 除法、int 与 long 比较自动提升 ——
  除 F1 的「最小值不随版本变」之外，守卫本身是对的。
- 四段魔数 + 段版本上界齐备：`ReplayDecoder.cs:89-91`（metadata）、`:146-148`（key events）、
  `:173-175`（hit snapshots）、`:218-220`（cursor）—— 未来版本显式拒绝而非静默错位。
- metadata 读写逐字段对称：`ReplayEncoder.cs:30-69` ↔ `ReplayDecoder.cs:79-136`（36 个字段顺序一致，
  `ver >= 2` 的三个追加字段两侧都按同一判据）。
- hit snapshot 读写对称：`ReplayEncoder.cs:87-109` ↔ `ReplayDecoder.cs:161-201`
  （v2 最小值 42 与写侧 1+可选 8 一致；F1 只涉及 v1）。
- key event 读写对称：`ReplayEncoder.cs:74-85` ↔ `ReplayDecoder.cs:138-159`（11B 精确）。
- cursor 段读写对称：`ReplayEncoder.cs:112-123` ↔ `ReplayDecoder.cs:209-231`（16B 精确）。
- 尾部段三态（无段 / 未知魔数 / 正常段）语义由 `CursorTrackCheck⑴⑵⑶` 钉住，不重复报。
- `Decode` 不校验 EOF、未知尾部整体忽略 —— 这是「旧构建读新文件」的设计，
  仅对已带压缩包装层的构建成立；上游 Spectre（无该包装层）读不了本模组的新文件，
  但本模组能读它的旧文件（`0x1F` 分支），属单向兼容，见「版本适配观察」。
- 加密构造无问题：随机 salt/IV（`ReplayCrypto.cs:73-80`）、PBKDF2 一次派生两把密钥（`:53-62`）、
  encrypt-then-MAC 且 MAC 覆盖 magic+version+iterations+salt+IV+body（`:244-262`）、
  先验 MAC 后解密（`:198-206`）、定长时间比较（`:268-275`）—— 均由 `ReplayCryptoCheck` 覆盖。
- v2 容器兼容（60000 迭代、头里无迭代字段）路径正确（`ReplayCrypto.cs:162-178`），
  由 `ReplayCryptoCheck⑧` + 夹具 `tools/UiPreview/fixtures/v2-container.bin` 钉住。
- 二进制端序：全字段经 `BinaryWriter`/`BinaryReader`（固定小端），与手写
  `WriteInt32`/`ReadInt32`（`ReplayCompression.cs:137-146`、`ReplayCrypto.cs:223-232`）一致；
  没有一次性 `BinaryFormatter`/`Marshal` 结构体直写，因此不存在 struct layout / 对齐风险。
- double/float 精度逐项核对（游戏侧类型已核实，见下表）：`SongPosition` double、
  `OverloadCounter` float、`CurrAngle/CachedAngle/TargetExitAngle` double —— 存储宽度与游戏一致。
  `CurrAngle` 经 `SpectreState.GetAngle` 收窄成 float 再存 double（`SpectreState.cs:463-468`），
  精度上限是 float（~1e-4 度），而它只用于「角度 ≥ CurrAngle」推进判断，不影响语义。
- `ReplayData.Reset()`（`ReplayData.cs:132-170`）逐字段覆盖全部 36 个成员，无遗漏；
  列表用 `Clear()` 保留实例 —— 与 `ToReplayData` 先 `Reset()` 再赋值的写法自洽（null 风险见 F5）。
- `ToReplayData`（`ReplayDecoder.cs:292-347`）对 `ReplayData` 的全部属性做到了 36/36 赋值，
  不会静默丢字段（`Replay` 层面的 `StartTime`/`SpVersion`/`EndTime` 见 F4）。
- `ReplayFileInfo.ParseJudgments`（`:182-202`）容错齐全：null/空串/非 `[` 开头/非数字/负数 → `null`。
- `ReplayFileInfo.JudgmentRow`（`:241-257`）用 `(uint)idx >= (uint)Length` 防越界（负数也挡住）。
- `ReplayFileInfo.JudgmentNames`（`:213-219`）与 r150 `HitMargin` 枚举 16 项**顺序逐项一致**，
  且 `JudgmentList` 的索引语义（`(int)margin`）与游戏 `hitMarginsCount[(int)hitMargin]`
  完全同源 —— 判定分布显示口径正确。
- `ReplayFileInfo.FromFile`（`:81-157`）异常全吞转 `Error`、`FileInfo` 另走 try —— OnGUI 安全。
- 编码失败不产生半成品：`ReplayEncoder.Encode` 全程在内存 `byte[]` 上完成，写盘唯一在
  `ReplayManager.SaveReplayNew`，其 `opened` 标记已覆盖「已截断后再失败 → 删文件」的常规路径
  （硬断电/进程被杀无原子替换，但那只影响本次新文件，不会毁掉既有回放）—— 不属于 Codec 缺陷。
- 尾部段中途截断（`remaining < 12`）静默降级为空轨迹：核心段（key/hit）的真损坏会响亮失败，
  只有指针轨会丢，与 `.psprp` 的可移植性设计相容，判定可接受。
- `ReplayDecoder.IsNewFormat`（`:41-49`）零调用属**既有裁决**（`tools/deadcode-audit.ps1:85`
  的 C 类「Spectre 有调用方、按裁决不补」白名单），不重复报；仅备忘：它对磁盘上的加密
  `.sprp` 恒返回 false（首字节 `0x53 'S'`），将来若有人把它接到「列表只认新格式」的过滤上，
  会把所有加密回放藏掉。
- `ReplayFileInfo.JudgmentTotal`（`:205-211`）实测零调用、全仓无消费者（`tools` 也没有）——
  已确认，不值得单开条目；若将来接 UI，注意 `int` 求和可被手改的 `JudgmentList`
  （每项 ≤ `int.MaxValue`）溢出成负数。

## 版本适配观察

| 本项目假设 | releaseNumber 150 的实际形状 | 结论 |
|---|---|---|
| `releaseNumber = 150` | `Releases.cs:3` `public const int releaseNumber = 150` | 一致 |
| `Conductor.songposition_minusi` 是 `double`（`KeyEvent.SongPosition`、`CursorSample.SongPosition` 用 double 存） | `scrConductor.cs:184` 字段 `private double _songposition_minusi`、`:241` `public double songposition_minusi` | 一致，无精度损失 |
| `HitSnapshot.OverloadCounter` 是 float | `scrFailBar.cs:22` `public float overloadCounter`（`scrPlayer.cs:13` `failBar` 为该类型） | 一致 |
| `CurrAngle/CachedAngle/TargetExitAngle` 需要 double | `scrPlanet.cs:37` `public double angle`、`:73` `public double cachedAngle`、`:159` `public double targetExitAngle { get; private set; }` | 一致 |
| `HitSnapshot.HitTick` 是 `long?` | `scrPlayer.cs:734` `public bool Hit(long? hitTick, bool isAuto = false)`；`scrPlanet.cs:648` `SwitchChosen(long? hitTick)`，消费点 `:977-980` | 一致；`HitSnapshotVersion = 2` 的必要性成立（r150 才有此概念） |
| `JudgmentList` 的下标 = `(int)HitMargin` | `HitMargin.cs:1-19` 16 个成员、顺序与 `ReplayFileInfo.JudgmentNames` 完全相同；`scrMarginTracker.cs:10` `int[] hitMarginsCount = new int[Max+1]`、`:139` `hitMarginsCount[(int)hitMargin] += count` | 一致 |
| `CursorSample` 的归一化依赖旧输入系统 `Input.mousePosition`（像素、左下原点、y 向上） | 游戏自己就在用：`CUIColorPicker.cs:84`、`DecoTransformGizmoHolder.cs:78`、`PauseLevel.cs:734`、`PracticeTimeline.cs:241`；Assembly-CSharp 内**没有任何** `UnityEngine.InputSystem` 引用 | 旧 Input 仍启用；Unity 6 的「两套输入系统」若将来切到新系统，`Input.mousePosition` 会抛 `InvalidOperationException`（需在升级时重新验证这条录制路径） |

**跨模组兼容（单向）**：上游 `D:\Projects\Spectre`（HEAD `1d84227`）**没有** `ReplayCompression.cs`，
`ReplayDecoder.cs:80-98` 仍是「整条 gzip 流 + 512MB 边读边限量」，没有任何
`Brotli`/`CompressionHeaderLen` 引用。因此：
- 上游产出的回放本模组能读（`ReplayCompression.cs:44-47` 的 `0x1F` 旧布局分支，为此刻意保留）；
- **本模组产出的 `.psprp` 上游读不了**（首字节是压缩算法号 `0x00/0x01/0x02`，上游直接
  `new GZipStream` 会抛 `InvalidDataException`）。
`ReplayDecoder.cs:203-207` 的「旧构建读新文件…双向兼容」只对**已经带压缩包装层的构建**成立
（包装层是 2026-10-18 本地引入的，不是从上游移植来的）。这不是缺陷，但意味着「换回上游模组」
时新回放会全部读不出来，值得在发布说明里写一句。

**审计口径备忘**：`docs/verify.md` 的「套件构成」表（`:37-59`）里**没有** `CursorTrackCheck`
这一行，而它确实在跑（`tools/UiPreview/Driver.cs:87` 已登记，文件 `CursorTrackCheck.cs` 共 12 条判据）
—— 属闸门文档漂移，建议补一行。它**只覆盖**往返/旧文件无段/未知尾部/
段版本上界/巨大 count（`0x7FFFFFFF`）/取样语义，**不覆盖**：v1 hit 段（F1）、
按比例放行的巨大 count（F2）、乱序轨迹（F6）。