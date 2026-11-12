# 回放按键 API（KeyViewer 拉取接口）

> **版本：** `SpectreAPI.ApiVersion >= "0.6"`
>
> **适用场景：** 其他键位显示模组（KeyViewer、CheryTools KeyViewer、自制键位显示器等）主动查询回放按键状态，替代被动的补丁桥接。

## 三种接入方式一览

| 方式 | 方向 | 适用模组 | 现状 |
|---|---|---|---|
| 本拉取 API | KV 主动调 SpectreReplay | 会主动轮询/订阅的自制 KV | **本文档** |
| 补丁桥（推） | SpectreReplay 补进 KV 的私有形状 | KeyViewer（`KeyInput.GetKey`）、CheryTools（`PollInputStates`） | 内置，自动 |
| 外部驱动契约（推） | SpectreReplay 调 KV 公开接口 | **凡公开 `ReplayInput` 类型的 KV**（RainingKeys、Quartz 等） | 内置，自动 |

**公开 `ReplayInput` 类型的 KV 无需本文档的 API**：这是一份跨模组通用约定（RainingKeys 0.5.0 反编译实证），受控模式下只认显式按键调用。SpectreReplay 的 `ReplayInputBridge` **按类型名扫描全部已加载程序集**（不绑程序集/命名空间，装几个亮几个），自动按契约驱动——回放开始 `OnStartInputs()` 进受控模式、每个按键边沿 `OnKeyPressed/OnKeyReleased(KeyCode)`、结束 `OnEndInputs()` 还原。键码由桥内部做 VK→Unity KeyCode 换算（SkyHook 逆链 + VK_APPS=93 特判）。如果你的自制 KV 也公开同名同形的 `ReplayInput` 类型，装上即被自动驱动，无需任何注册。

## 为什么需要这个接口

回放的按键不经过 Unity 的 `Input`，也不经过 SkyHook 的真实钩子广播。键位显示器只读这两条链的话，回放期间就是一片漆黑。此前 SpectreReplay 用 Harmony 补丁往 KeyViewer 里注入（推模式），但每接入一个键位显示模组就要写一座桥。这个接口把方向反过来：**SpectreReplay 只暴露只读状态，谁来拉、谁来渲染是它自己的事。**

## 快速开始

### 1. 检测是否可用

```csharp
// 硬依赖：直接引用 SpectreReplay.dll，ApiVersion 仅供诊断展示
Debug.Log("SpectreReplay API " + SpectreAPI.ApiVersion); // "0.6" 及以上

// 软依赖：反射探测成员（推荐——**不要比较版本号字符串**，
// "0.10" 按字典序小于 "0.6"，将来版本升上去会误判）
var asm = AppDomain.CurrentDomain.GetAssemblies()
    .FirstOrDefault(a => a.GetName().Name == "SpectreReplay");
var apiType = asm?.GetType("SpectreReplay.API.SpectreAPI");
bool available = apiType != null
    && apiType.GetMethod("GetHeldReplayKeys") != null
    && apiType.GetEvent("ReplayKeyChanged") != null;
```

### 2. 每帧轮询（推荐）

```csharp
void Update()
{
    if (!SpectreAPI.IsReplayActive) return;

    // 方式 A：批量拉取当前按住的键
    ushort[] held = SpectreAPI.GetHeldReplayKeys();
    foreach (ushort vk in held)
    {
        // vk 是 Win32 虚拟键码（如 0x20=Space, 0x41=A, 0x25=LeftArrow）
        RenderKeyHeld(vk);
    }

    // 方式 B：单键查询（你已有自己的键码表时更省）
    foreach (var keyCode in myBoundKeys)
    {
        ushort vk = SpectreAPI.UnityKeyToVk(keyCode);
        bool held = SpectreAPI.IsReplayKeyHeld(vk);
        bool justPressed = SpectreAPI.WasReplayKeyPressedThisFrame(vk);
        RenderKey(keyCode, held, justPressed);
    }
}
```

### 3. 事件驱动（按下/抬起时通知）

```csharp
void OnEnable()
{
    SpectreAPI.ReplayKeyChanged += OnReplayKey;
}

void OnDisable()
{
    SpectreAPI.ReplayKeyChanged -= OnReplayKey;
}

void OnReplayKey(ushort vk, bool pressed)
{
    // vk 是 Win32 虚拟键码
    // pressed = true 为按下，false 为抬起
    // 主线程触发，你的回调异常不会伤到回放管道
    OnKeyStateChanged(vk, pressed);
}
```

> **注意：** 事件只在状态**真正翻转**时触发。回放数据里连续的 down/down 或 up/up 会被幂等过滤，不会重复发。

## API 参考

| 成员 | 类型 | 说明 |
|---|---|---|
| `IsReplayActive` | `bool` 属性 | 回放是否正在进行（暂停菜单打开时仍为 true）。任意线程可读。 |
| `IsReplayKeyHeld(ushort vk)` | `bool` 方法 | 指定 VK 此刻是否被回放按住。 |
| `WasReplayKeyPressedThisFrame(ushort vk)` | `bool` 方法 | 指定 VK 是否在本帧产生了按下沿。 |
| `GetHeldReplayKeys()` | `ushort[]` 方法 | 当前所有按住的 VK 快照。无回放时返回空数组。 |
| `UnityKeyToVk(KeyCode code)` | `ushort` 方法 | Unity KeyCode → Win32 VK 换算。失败返回 0。 |
| `ReplayKeyChanged` | `event Action<ushort, bool>` | 按键状态变化事件。主线程触发，异常隔离。 |
| `PlaybackStarted` / `PlaybackStopped` | `event Action` | 回放启停事件（0.5 版已有）。 |

## 键码约定

所有 `ushort vk` 参数都是 **Win32 虚拟键码**（Virtual-Key Codes），不是 Unity KeyCode：

| 键 | Win32 VK | Unity KeyCode |
|---|---|---|
| Space | `0x20` (32) | `KeyCode.Space` (32) |
| A | `0x41` (65) | `KeyCode.A` (97) |
| Left Arrow | `0x25` (37) | `KeyCode.LeftArrow` (276) |
| Left Shift | `0xA0` (160) | `KeyCode.LeftShift` (304) |
| Menu (Apps) | `0x5D` (93) | `KeyCode.Menu` (319，Unity 6000.3.25f1 实测) |

**换算用 `SpectreAPI.UnityKeyToVk()`**，不要自己写映射表——SkyHook 的 KeyLabel 表有缺键（如 Menu），那个方法里已经做了兜底。

**键码域是「游戏实际收到的键」。** 如果玩家在 SpectreReplay 的键位重映射里把键 X 映射成了键 Y，回放期间 API 报告的是 Y（游戏判定真正消费的那颗键）。你的键位显示配置里渲染哪颗键，就拿它的 Unity KeyCode 走 `UnityKeyToVk` 查询——与游戏判定层完全一致。

## 注意事项

- **只读快照，绝不外借内部可变结构。** 每次调用 `GetHeldReplayKeys()` 都返回一个新数组。
- **`WasPressedThisFrame` 的帧语义与 Unity 的 `Time.frameCount` 一致。** 如果你在 `Update` 里查，拿到的就是本帧的按下沿。
- **非回放期间所有查询返回 false / 空数组。** 不需要你自己判断 `IsReplayActive` 再决定要不要渲染——但判断一下可以省掉每帧的数组分配。
- **游戏内暂停菜单打开时 `IsReplayActive` 仍为 true。** 暂停只是歌曲时间不走、不再消费新事件；已按住的键在 API 里保持按住状态，与你渲染的画面一致。
