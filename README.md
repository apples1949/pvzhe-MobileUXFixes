# 手机操作优化（MobileUXFixes）

> 手机版两个误操作修正：**① 选卡界面想上下滑动却误选了植物**、**② 战斗中误触植物卡后无法取消**。

| 项目 | 内容 |
| --- | --- |
| Mod ID | `mobileuxfixes` |
| 程序集 | `JTYMobileUXFixes` → 包内 `Runtime/ModAssembly.dll` |
| 入口类 | `MobileUXFixesEntry` |
| 当前版本 | 1.2.0 |
| 分类 | 手机适配 |

> ⚠️ 本包**只含 ①②**。原来的 ③按压反馈 / ④自动拾取开关 已于 2026-09-30
> **拆到独立包** [`pvzhe-MobileTapFeedback`](https://github.com/apples1949/pvzhe-MobileTapFeedback)，
> 本包**不再包含**它们。**两个包要分别构建、分别装机。**

## ① 选卡界面"想上下滑动却选中了植物"

**根因**：卡池卡的 `Pressed()` 在**松手时**触发（Godot `BaseButton` 默认
`ACTION_MODE_BUTTON_RELEASE`），滚动与点击**同时发生** ⇒ 滑一下就误选一张。

**方案**：记录按下位置，**松手时位移 ≥ 阈值** ⇒ 调 `PacketChoose(card)` 把刚误选的**撤掉**
（与玩家手动点两下取消走**完全相同**的原生路径）。

**①b 自制关卡的「编辑卡牌库」是另一套卡池**（`LevelEditorPacketBank`），同一问题也在
⇒ 那里的撤销 = `PacketPickRelease()` + `card.Reset()`。

## ② 战斗中误触植物卡后无法取消

**根因**：`PacketPickControl.PickPacket()` 只有 `if (_packet.select)` 一个分支、**没有 `else`**
⇒ 再点一次已拿起的卡等于**什么都没发生**。

**方案**：记录"点击前" `packetPick` 是哪张卡；**再点同一张** ⇒ 调 `PacketPickRelease()`
（游戏自己"放弃选择"的路径）。

> ⚠️ 用 `PacketPickRelease()` 而**不是** `Release()` —— 后者会**连带收掉铲子/手套工具**。

## 内部开关（默认全开）

```csharp
internal static readonly bool FixScrollMisPick = true;   // ①（含 ①b 自制关卡编辑卡牌库）
internal static readonly bool FixPickCancel    = true;   // ②
private  static readonly bool EnableLog        = false;  // 诊断日志
private  static readonly bool DiagVerbose      = false;  // 逐事件日志（噪音很大）
```

改开关需**重新编译打包**。

## 目录结构

```
MobileUXFixes/
├── mod.json
├── build_mod.py
├── runtime_src/MobileUXFixesEntry.cs
├── Runtime/ModAssembly.dll
└── dist/MobileUXFixes.pmod
```

## 构建

```powershell
python mods\MobileUXFixes\build_mod.py             # 编译 + 打包
python mods\MobileUXFixes\build_mod.py --install   # 继续装机
```

> 两个包互不依赖，改完**分别打包**。

## 硬护栏（违反会整包被拒）

1. `mod.json` 必须在**根**且**唯一**
2. `Runtime/` 下**只允许** `ModAssembly.dll`
3. 包内**绝不允许**出现 `.cs`（`ModLoader.IsExecutablePackageFile` 白名单拒收）

## 已知坑

1. **误选判定必须在"抬起"侧做** —— 卡池卡的 `Pressed` 就是松手时触发，按下时判定拿不到位移。
2. **手写 csproj ⇒ 没有 Godot 源码生成器** ⇒ 自定义 `_Process` / `_Input` 不会被调用；
   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))`。
3. **`TowerDefenseInGamePacketShow` 是被到处复用的卡类**（卡池/卡槽/图鉴/商店）
   ⇒ 对它做任何操作前**必须先用祖先链确认它在哪个界面**。
4. **卡走对象池**：`ResetForPool()` 会 `ClearEventHandlers()` ⇒ 事件钩子必须按
   "在不在目标容器里"**逐帧重挂**，不能只挂一次。① 干脆**不用事件钩子**，改为
   **逐帧读状态边沿**（更稳）。
5. **别用 `Release()`** 取消选卡 —— 它会连带收掉铲子/手套工具，要用 `PacketPickRelease()`。

## 诊断

```csharp
private static readonly bool EnableLog   = false;   // ★ v1.2.0 真机复验通过后关闭
private static readonly bool DiagVerbose = false;   // 逐事件日志，排查时序时才开
```

①② 已确认为"抬起判定 + 手势簿记"，**正常运行不输出任何日志**；需要排查时临时置 `true` 重新打包。

## 与相关 Mod 的关系

| Mod | 内容 |
| --- | --- |
| **本包** `mobileuxfixes` | ① 选卡滑动误选撤销 / ② 战斗误触取消 |
| [`pvzhe-MobileTapFeedback`](https://github.com/apples1949/pvzhe-MobileTapFeedback) | ③ 按压反馈 / ④ 自动拾取开关 |

## 版本历史

| 版本 | 变更 |
| --- | --- |
| 1.2.0 | 真机复验通过；③④ 拆到独立包 `MobileTapFeedback`；关闭诊断日志 |
| 1.1.0 | 增加 ①b 自制关卡编辑卡牌库的误选撤销 |
| 1.0.0 | 首个版本 |
