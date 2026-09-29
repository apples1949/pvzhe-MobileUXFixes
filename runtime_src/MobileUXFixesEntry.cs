using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「手机操作优化」Mod 的托管运行时入口。
///
/// ── 四个功能（对应手机版实测问题，2026-09-29）────────────────────
///
/// ① 选卡界面"想上下滑动却选中了植物"
///    根因：卡池卡的 `Pressed()` 在**松手时**触发（Godot `BaseButton` 默认
///    `ACTION_MODE_BUTTON_RELEASE`），滚动与点击同时发生 ⇒ 滑一下就误选一张。
///    方案：记录按下位置，松手时位移 ≥ 阈值 ⇒ 调 `PacketChoose(card)` 把刚误选的撤掉
///    （与玩家手动点两下取消走完全相同的原生路径）。
///
/// ② 战斗中误触植物卡后无法取消
///    根因：`PacketPickControl.PickPacket()` 只有 `if (_packet.select)` 一个分支，**没有 else**
///    ⇒ 再点一次已拿起的卡等于什么都没发生。
///    方案：记录"点击前" `packetPick` 是哪张卡；再点同一张 ⇒ 调 `PacketPickRelease()`
///    （游戏自己"放弃选择"的路径；不用 `Release()`，那会连带收掉铲子/手套工具）。
///
/// ③ 关卡/章节点击无反馈
///    根因：`DragMenuSelectItem`（关卡 `DragMenuSelectItemlevel`、章节
///    `DragMenuSelectItemChapter` 的共同基类）只挂了 `button.Pressed`，**没有按压视觉**。
///    方案：挂 `Button.ButtonDown / ButtonUp / MouseExited`，把 `graphics` 压暗 + 缩小，
///    抬起还原 —— 与游戏自家 `NinePatchButtonBase` 用 `ButtonDown` 做按压视觉同一套路。
///
/// ④ 无法关闭自动拾取阳光 / 金币
///    根因（源码实证，两个独立存档特性键）：
///        阳光 `TowerDefenseSunBase`:  autoCollect = GetFeatureValue("SunCollect") != 0
///        金币 `TowerDefenseCoinBase`: autoCollect = GetFeatureValue("CoinCollect") > 0 && 场上无吸金磁
///    游戏没给玩家任何入口去改。方案：在 **图鉴 → 工具页（PropLayer）** 注入一个
///    「自动拾取」框，含两个独立开关（阳光 / 金币），点击写
///    `GameSaveManager.SetFeatureValue(...)`（进存档，重启保留），并同步场上
///    已生成的阳光/金币（这两个类的 `autoCollect` 是 public 字段）。
///
/// ── 铁律（前几个 Mod 已定案）────────────────────────────────────
/// · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
/// · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · `TowerDefenseInGamePacketShow` 是被到处复用的卡类（卡池/卡槽/图鉴/商店），
///   对卡做任何操作前必须先用祖先链确认它在哪个界面。
/// · 卡走对象池，`ResetForPool()` 会 `ClearEventHandlers()` ⇒ 事件钩子必须按
///   "在不在目标容器里"逐帧重挂，不能只挂一次。
/// </summary>
public sealed class MobileUXFixesEntry : IXWModRuntimeEntry
{
	private const string P = "[MobileUXFixes] ";

	// ================================================================ 内部开关（默认全开）

	/// <summary>① 选卡界面滑动误选撤销。</summary>
	internal static readonly bool FixScrollMisPick = true;

	/// <summary>② 战斗中再点同一张卡 = 取消选择。</summary>
	internal static readonly bool FixPickCancel = true;

	/// <summary>③ 关卡/章节按压视觉反馈。</summary>
	internal static readonly bool FixPressVisual = true;

	/// <summary>④ 图鉴-工具页"自动拾取"开关框（阳光 / 金币 两个独立开关）。</summary>
	internal static readonly bool FixAutoCollect = true;

	/// <summary>诊断日志（默认关；要排障改回 true 重编）。</summary>
	private static readonly bool EnableLog = false;

	/// <summary>① 判定"是滑动不是点击"的位移阈值（视口逻辑像素）。</summary>
	private const float DragThresholdPx = 24f;

	private const string MetaBankHooked = "MUXF_BankHooked";
	private const string MetaSlotHooked = "MUXF_SlotHooked";
	private const string MetaItemHooked = "MUXF_ItemHooked";
	private const string MetaAlmanacBox = "MUXF_AlmanacBox";

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	/// <summary>① 左键按下时的位置（视口坐标）；无效时 _downValid=false。</summary>
	private Vector2 _downPos;
	private bool _downValid;
	private bool _prevDown;

	/// <summary>② 上一帧"当前拿起的卡"（点击前状态，用于识别"再点同一张"）。</summary>
	private ulong _prevPickId;

	/// <summary>③ 已挂钩的关卡/章节入口（强引用，便于节点销毁后清理）。</summary>
	private readonly List<DragMenuSelectItem> _hookedItems = new List<DragMenuSelectItem>();
	/// <summary>③ 挂钩时记录的 graphics 原始 Modulate / Scale（抬起还原用）。</summary>
	private readonly Dictionary<ulong, Color> _origModulate = new Dictionary<ulong, Color>();
	private readonly Dictionary<ulong, Vector2> _origScale = new Dictionary<ulong, Vector2>();

	/// <summary>扫描节流（视觉反馈与图鉴注入不需要每帧扫）。</summary>
	private int _frame;

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			Log("初始化完成（滑动误选撤销 / 误触取消 / 关卡按压反馈 / 图鉴自动拾取开关）。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Initialize 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void OnAllModsLoaded()
	{
		try
		{
			if (_started)
			{
				return;
			}
			_tree = Engine.GetMainLoop() as SceneTree;
			if (_tree == null)
			{
				Log("拿不到 SceneTree，本 Mod 不生效。");
				return;
			}
			_tick = Callable.From(new Action(OnFrame));
			_tree.Connect("process_frame", _tick);
			_started = true;
			Log("已挂载 process_frame。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "OnAllModsLoaded 异常（已吞）：" + ex.Message); } catch { }
		}
	}

	public void Shutdown()
	{
		try
		{
			if (_started && _tree != null && GodotObject.IsInstanceValid(_tree))
			{
				_tree.Disconnect("process_frame", _tick);
			}
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "Shutdown 异常（已吞）：" + ex.Message); } catch { }
		}
		finally
		{
			_started = false;
		}
	}

	// ================================================================ 每帧驱动

	private void OnFrame()
	{
		try
		{
			if (_tree == null || !GodotObject.IsInstanceValid(_tree))
			{
				return;
			}
			_frame++;

			TrackMouseAndPick();                       // ①②共用的逐帧采样
			ScanPacketShows();                         // ①② 事件钩子维护
			if (_frame % 15 == 0)
			{
				ScanSelectItems();                     // ③ 关卡/章节按压反馈
				ScanAlmanac();                         // ④ 图鉴注入自动拾取框
			}
		}
		catch (Exception ex)
		{
			if (_diag < 100)
			{
				_diag = 100;
				Log("每帧驱动异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ①② 逐帧采样

	/// <summary>
	/// ① 记录"左键刚按下"的位置（OnPressed 在松手时触发，需要按下的起点算位移）；
	/// ② 记录"本帧结束时当前拿起的卡"（下一帧的点击发生前，它就是点击前状态）。
	/// ★ 时序：输入派发（含 OnPressed 回调）发生在本帧 process_frame 之前，
	///   所以这里写的值对"下一帧的点击"而言就是点击前状态。
	/// </summary>
	private void TrackMouseAndPick()
	{
		try
		{
			// ── ① 按下位置 ───────────────────────────────────────────
			bool down = Input.IsMouseButtonPressed(MouseButton.Left);
			if (down && !_prevDown)
			{
				_downPos = GetMousePos();
				_downValid = true;
			}
			else if (!down && _prevDown)
			{
				_downValid = false;        // ★ 抬起才失效：OnPressed 在抬起瞬间触发，要用得到
			}
			_prevDown = down;

			// ── ② 当前拿起的卡 ──────────────────────────────────────
			_prevPickId = 0;
			PacketPickControl ppc = GetPickControl();
			if (ppc != null)
			{
				TowerDefenseInGamePacketShow pick = ppc.packetPick;
				if (pick != null && GodotObject.IsInstanceValid(pick))
				{
					_prevPickId = pick.GetInstanceId();
				}
			}
		}
		catch { }
	}

	/// <summary>①② 给卡挂事件钩子：按"这张卡现在在不在目标容器里"逐帧维护。</summary>
	private void ScanPacketShows()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			bool inChoose = false;
			TowerDefenseInGameSeedBank seedBank = mgr.GetSeedBank();
			if (seedBank != null && GodotObject.IsInstanceValid(seedBank))
			{
				inChoose = !seedBank.hasGameStarted;      // 选卡阶段
			}

			var cards = new List<Node>();
			CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, "TowerDefenseInGamePacketShow", cards, 500);

			// ★ 收集"目标容器成员"集合，容器外的同名卡清掉钩子标记
			//   （卡走对象池，ResetForPool → ClearEventHandlers 会把我们挂的事件清掉，
			//     若标记还留着就永远不会重挂 ⇒ 功能静默失效）。
			var bankMembers = new HashSet<ulong>();
			var slotMembers = new HashSet<ulong>();

			foreach (Node node in cards)
			{
				TowerDefenseInGamePacketShow c = node as TowerDefenseInGamePacketShow;
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				ulong id = c.GetInstanceId();

				// ── ① 选卡界面卡池的卡 ──────────────────────────────
				if (FixScrollMisPick && IsInChooseBank(c))
				{
					bankMembers.Add(id);
					if (!c.HasMeta(MetaBankHooked))
					{
						c.SetMeta(MetaBankHooked, true);
						c.OnPressed += OnBankCardPressed;
					}
				}
				// ── ② 战斗卡槽的卡（只在战斗期挂）────────────────────
				else if (FixPickCancel && inChoose == false && seedBank != null
					&& GodotObject.IsInstanceValid(seedBank) && InList(seedBank.packetList, c))
				{
					slotMembers.Add(id);
					if (!c.HasMeta(MetaSlotHooked))
					{
						c.SetMeta(MetaSlotHooked, true);
						c.OnPressed += OnSlotCardPressed;
					}
				}
			}

			// 容器外的同名卡：清标记（下次进容器会重新挂；此刻事件已被对象池清空）
			foreach (Node node in cards)
			{
				TowerDefenseInGamePacketShow c = node as TowerDefenseInGamePacketShow;
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				ulong id = c.GetInstanceId();
				if (!bankMembers.Contains(id) && c.HasMeta(MetaBankHooked))
				{
					c.RemoveMeta(MetaBankHooked);
				}
				if (!slotMembers.Contains(id) && c.HasMeta(MetaSlotHooked))
				{
					c.RemoveMeta(MetaSlotHooked);
				}
			}
		}
		catch (Exception ex)
		{
			if (_diag < 101)
			{
				_diag = 101;
				Log("卡钩子维护异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>① 卡池卡被松手按下：若"按下→松开"位移超阈值 ⇒ 视为滑动，撤销误选。</summary>
	private void OnBankCardPressed(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (card == null || !GodotObject.IsInstanceValid(card) || !_downValid)
			{
				return;
			}
			Vector2 now = GetMousePos();
			float dist = (now - _downPos).Length();
			if (dist < DragThresholdPx)
			{
				return;                        // 正常点击，不干预
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseBattleFeaturePacketBank feature = mgr.GetPacketBankFeature();
			if (feature == null || !GodotObject.IsInstanceValid(feature))
			{
				return;
			}
			// 与玩家手动点两下取消走同一条原生路径：CancelPacketAnimation + DeletePacket + PacketAlive
			feature.PacketChoose(card);
			Log("滑动误选已撤销（位移 " + (int)dist + "px）：" + SafeKey(card));
		}
		catch (Exception ex)
		{
			if (_diag < 102)
			{
				_diag = 102;
				Log("滑动撤销异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>② 战斗卡槽卡被按下：若点击前它就是"当前拿起的卡" ⇒ 取消选择。</summary>
	private void OnSlotCardPressed(TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (card == null || !GodotObject.IsInstanceValid(card) || _prevPickId == 0)
			{
				return;
			}
			if (_prevPickId != card.GetInstanceId())
			{
				return;                        // 点的是别的卡 ⇒ 交给游戏正常选中
			}
			PacketPickControl ppc = GetPickControl();
			if (ppc == null)
			{
				return;
			}
			ppc.PacketPickRelease();           // 只取消选择，不碰铲子/手套等工具
			Log("已取消误触的植物：" + SafeKey(card));
		}
		catch (Exception ex)
		{
			if (_diag < 103)
			{
				_diag = 103;
				Log("取消误触异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ③ 关卡/章节按压反馈

	/// <summary>
	/// 给所有 `DragMenuSelectItem`（关卡/章节共同基类）挂按压视觉：
	/// ButtonDown → graphics 压暗 0.62 + 缩放 ×0.94；ButtonUp / MouseExited → 还原。
	/// 缩放必须用"乘"不能用"设"——`InitLevel()` 会把某些入口 `graphics.Scale = 0.5`。
	/// </summary>
	private void ScanSelectItems()
	{
		try
		{
			// 清掉已销毁的条目，避免列表无限增长
			_hookedItems.RemoveAll(x => x == null || !GodotObject.IsInstanceValid(x));

			var items = new List<Node>();
			CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, "DragMenuSelectItem", items, 200);
			foreach (Node node in items)
			{
				DragMenuSelectItem item = node as DragMenuSelectItem;
				if (item == null || !GodotObject.IsInstanceValid(item) || item.button == null)
				{
					continue;
				}
				if (item.HasMeta(MetaItemHooked))
				{
					continue;
				}
				Control g = item.graphics;
				if (g == null || !GodotObject.IsInstanceValid(g))
				{
					continue;
				}
				item.SetMeta(MetaItemHooked, true);
				_hookedItems.Add(item);
				ulong id = item.GetInstanceId();
				_origModulate[id] = g.Modulate;
				_origScale[id] = g.Scale;

				item.button.ButtonDown += () =>
				{
					try
					{
						if (!GodotObject.IsInstanceValid(g))
						{
							return;
						}
						Color m = _origModulate.TryGetValue(id, out Color om) ? om : g.Modulate;
						Vector2 s = _origScale.TryGetValue(id, out Vector2 os) ? os : g.Scale;
						g.Modulate = new Color(m.R * 0.62f, m.G * 0.62f, m.B * 0.62f, m.A);
						g.Scale = s * 0.94f;
					}
					catch { }
				};
				Action restore = () =>
				{
					try
					{
						if (!GodotObject.IsInstanceValid(g))
						{
							return;
						}
						if (_origModulate.TryGetValue(id, out Color om))
						{
							g.Modulate = om;
						}
						if (_origScale.TryGetValue(id, out Vector2 os))
						{
							g.Scale = os;
						}
					}
					catch { }
				};
				item.button.ButtonUp += restore;
				item.button.MouseExited += restore;
			}
		}
		catch (Exception ex)
		{
			if (_diag < 104)
			{
				_diag = 104;
				Log("关卡按压反馈异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	// ================================================================ ④ 图鉴-工具页 自动拾取开关

	/// <summary>
	/// 找到图鉴（Almanac）就往**工具页（PropLayer）**注入「自动拾取」框。
	/// 图鉴的页签切换就是 `propLayer.Visible` 切换 ⇒ 挂在它下面，可见性天然跟工具页一致。
	/// 每次打开图鉴都会新建对话框节点 ⇒ 用 meta 防重复注入，节点关了标记随节点一起没了。
	/// </summary>
	private void ScanAlmanac()
	{
		try
		{
			if (!FixAutoCollect)
			{
				return;
			}
			var list = new List<Node>();
			CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, "Almanac", list, 20);
			foreach (Node node in list)
			{
				Almanac al = node as Almanac;
				if (al == null || !GodotObject.IsInstanceValid(al) || al.HasMeta(MetaAlmanacBox))
				{
					continue;
				}
				if (al.propLayer == null || !GodotObject.IsInstanceValid(al.propLayer))
				{
					continue;
				}
				al.SetMeta(MetaAlmanacBox, true);
				BuildAutoCollectBox(al);
				Log("已在图鉴-工具页注入「自动拾取」开关框。");
			}
		}
		catch (Exception ex)
		{
			if (_diag < 105)
			{
				_diag = 105;
				Log("图鉴注入异常（本条只报一次）：" + ex.Message);
			}
		}
	}

	/// <summary>构建「自动拾取」框：标题 + 阳光开关 + 金币开关（两个独立开关）。</summary>
	private void BuildAutoCollectBox(Almanac al)
	{
		PanelContainer panel = new PanelContainer();
		panel.Name = "MUXFAutoCollectBox";
		// ★ v1.0.2：改到**右上角**（用户指定；v1.0.1 是左上角）。
		//   CanvasLayer 的 Control 子节点以视口为锚定区域。
		panel.AnchorLeft = 1f;
		panel.AnchorTop = 0f;
		panel.AnchorRight = 1f;
		panel.AnchorBottom = 0f;
		panel.OffsetLeft = -304f;
		panel.OffsetTop = 24f;
		panel.OffsetRight = -24f;
		panel.OffsetBottom = 184f;
		panel.GrowHorizontal = Control.GrowDirection.Begin;
		panel.GrowVertical = Control.GrowDirection.End;

		VBoxContainer box = new VBoxContainer();
		box.Name = "Box";
		box.AddThemeConstantOverride("separation", 6);
		panel.AddChild(box);

		Label title = new Label();
		title.Text = "自动拾取";
		box.AddChild(title);

		bool sunOn = GetFeature("SunCollect") != 0;
		bool coinOn = GetFeature("CoinCollect") > 0;

		CheckBox sun = MakeToggle("阳光", sunOn, v => ApplyAutoCollect("SunCollect", v, sunOnly: true));
		CheckBox coin = MakeToggle("金币", coinOn, v => ApplyAutoCollect("CoinCollect", v, sunOnly: false));
		box.AddChild(sun);
		box.AddChild(coin);

		al.propLayer.AddChild(panel);
	}

	private CheckBox MakeToggle(string text, bool initial, Action<bool> onChanged)
	{
		CheckBox cb = new CheckBox();
		cb.Text = text;
		cb.ButtonPressed = initial;
		cb.Pressed += () =>
		{
			try
			{
				onChanged(cb.ButtonPressed);
			}
			catch (Exception ex)
			{
				try { GD.PrintErr(P + "开关异常：" + ex.Message); } catch { }
			}
		};
		return cb;
	}

	/// <summary>读写存档特性键，并同步场上已生成的阳光/金币（让改动立即生效）。</summary>
	private void ApplyAutoCollect(string key, bool on, bool sunOnly)
	{
		GameSaveManager.Instance.SetFeatureValue(key, on ? 1 : 0);
		Log("自动拾取[" + key + "] = " + on);

		// 场上已生成的实例：autoCollect 是 public 字段，直接同步
		var nodes = new List<Node>();
		CollectByClassName(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
			0, sunOnly ? "TowerDefenseSunBase" : "TowerDefenseCoinBase", nodes, 500);
		bool goldMagnet = _tree != null && GodotObject.IsInstanceValid(_tree)
			&& _tree.GetNodeCountInGroup("GoldMagnet") > 0;
		foreach (Node n in nodes)
		{
			try
			{
				if (sunOnly)
				{
					if (n is TowerDefenseSunBase s && GodotObject.IsInstanceValid(s))
					{
						s.autoCollect = on;
					}
				}
				else if (n is TowerDefenseCoinBase c && GodotObject.IsInstanceValid(c))
				{
					// 与游戏原判据保持一致：场上有吸金磁时金币不自动收
					c.autoCollect = on && !goldMagnet;
				}
			}
			catch { }
		}
	}

	private static int GetFeature(string key)
	{
		try
		{
			return GameSaveManager.Instance.GetFeatureValue(key);
		}
		catch
		{
			return 0;
		}
	}

	// ================================================================ 工具

	private Vector2 GetMousePos()
	{
		try
		{
			Viewport vp = _tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null;
			if (vp != null && GodotObject.IsInstanceValid(vp))
			{
				return vp.GetMousePosition();
			}
		}
		catch { }
		return Vector2.Zero;
	}

	private PacketPickControl GetPickControl()
	{
		try
		{
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return null;
			}
			return mgr.GetPacketPickControl();
		}
		catch
		{
			return null;
		}
	}

	/// <summary>这张卡是否挂在**选卡界面的卡池**（`TowerDefenseInGamePacketBank`）之下。</summary>
	private static bool IsInChooseBank(Node node)
	{
		try
		{
			for (Node p = node?.GetParent(); p != null; p = p.GetParent())
			{
				if (p.GetType().Name == "TowerDefenseInGamePacketBank")
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static bool InList(Godot.Collections.Array<TowerDefenseInGamePacketShow> list,
		TowerDefenseInGamePacketShow card)
	{
		try
		{
			if (list == null || card == null)
			{
				return false;
			}
			ulong id = card.GetInstanceId();
			foreach (TowerDefenseInGamePacketShow c in list)
			{
				if (c != null && GodotObject.IsInstanceValid(c) && c.GetInstanceId() == id)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	private static string SafeKey(TowerDefenseInGamePacketShow card)
	{
		try
		{
			TowerDefensePacketConfig cfg = card.config;
			return (cfg != null && GodotObject.IsInstanceValid(cfg)) ? cfg.saveKey : "?";
		}
		catch
		{
			return "?";
		}
	}

	/// <summary>按类名递归收集节点（深度优先，最多 max 个）。</summary>
	private static void CollectByClassName(Node node, int depth, string className,
		List<Node> outList, int max)
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node.GetType().Name == className)
			{
				outList.Add(node);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByClassName(node.GetChild(i), depth + 1, className, outList, max);
			}
		}
		catch { }
	}

	private void Log(string msg)
	{
		if (!EnableLog)
		{
			return;
		}
		try { GD.Print(P + msg); } catch { }
	}
}
