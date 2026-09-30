using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using PVZHE.ModEditor.ModSystem;

/// <summary>
/// 「手机操作优化」Mod 的托管运行时入口。
///
/// ★ 本包**只保留两个功能**：「③关卡/章节按压反馈」与「④图鉴自动拾取开关」
///   已于 2026-09-30 **拆到独立新包**「点击反馈与自动拾取」（`MobileTapFeedback`）
///   —— 用户要求把这两项分离开，本包不再包含它们。
///
/// ── 两个功能（对应手机版实测问题，2026-09-29）────────────────────
///
/// ① 选卡界面"想上下滑动却选中了植物"
///    根因：卡池卡的 `Pressed()` 在**松手时**触发（Godot `BaseButton` 默认
///    `ACTION_MODE_BUTTON_RELEASE`），滚动与点击同时发生 ⇒ 滑一下就误选一张。
///    方案：记录按下位置，松手时位移 ≥ 阈值 ⇒ 调 `PacketChoose(card)` 把刚误选的撤掉
///    （与玩家手动点两下取消走完全相同的原生路径）。
///    ①b 自制关卡的"编辑卡牌库"是**另一套卡池**（`LevelEditorPacketBank`），
///    同一问题也在 ⇒ 那里撤销 = `PacketPickRelease()` + `card.Reset()`。
///
/// ② 战斗中误触植物卡后无法取消
///    根因：`PacketPickControl.PickPacket()` 只有 `if (_packet.select)` 一个分支，**没有 else**
///    ⇒ 再点一次已拿起的卡等于什么都没发生。
///    方案：记录"点击前" `packetPick` 是哪张卡；再点同一张 ⇒ 调 `PacketPickRelease()`
///    （游戏自己"放弃选择"的路径；不用 `Release()`，那会连带收掉铲子/手套工具）。
///
/// ── 铁律（前几个 Mod 已定案）────────────────────────────────────
/// · 手写 csproj 没有 Godot 源码生成器 ⇒ 自定义 Node 子类的引擎回调不会被调用，
///   全部逻辑走 `SceneTree.Connect("process_frame", Callable.From(Action))` 信号通道。
/// · Initialize / OnAllModsLoaded / Shutdown 一律 try/catch 吞异常（抛出 = 整包回滚）。
/// · `TowerDefenseInGamePacketShow` 是被到处复用的卡类（卡池/卡槽/图鉴/商店），
///   对卡做任何操作前必须先用祖先链确认它在哪个界面。
/// · 卡走对象池，`ResetForPool()` 会 `ClearEventHandlers()` ⇒ 事件钩子必须按
///   "在不在目标容器里"逐帧重挂，不能只挂一次（更稳的做法见 ①：干脆不用事件钩子，
///   改为逐帧读状态边沿）。
/// </summary>
public sealed class MobileUXFixesEntry : IXWModRuntimeEntry
{
	private const string P = "[MobileUXFixes] ";

	// ================================================================ 内部开关（默认全开）

	/// <summary>① 选卡界面滑动误选撤销（含 ①b 自制关卡编辑卡牌库）。</summary>
	internal static readonly bool FixScrollMisPick = true;

	/// <summary>② 战斗中再点同一张卡 = 取消选择。</summary>
	internal static readonly bool FixPickCancel = true;

	/// <summary>
	/// 诊断日志（★ v1.2.0 真机复验通过后关闭）：①② 已确认为"抬起判定 + 手势簿记"，
	/// 不再输出正常运行日志。需要排查时临时置 true。
	/// </summary>
	private static readonly bool EnableLog = false;

	/// <summary>逐事件日志（窗口输入逐条打点；噪音很大，排查具体时序时才置 true）。</summary>
	private static readonly bool DiagVerbose = false;

	/// <summary>① 判定"是滑动不是点击"的位移阈值（视口逻辑像素）。</summary>
	private const float DragThresholdPx = 24f;

	private SceneTree _tree;
	private Callable _tick;
	private bool _started;
	private int _diag;

	/// <summary>① 按下时的位置（视口坐标；由窗口输入事件驱动，见 OnWindowInputRelay）。</summary>
	private Vector2 _downPos;
	private bool _downValid;
	/// <summary>① 卡池卡上一帧的 `select` 状态（检测"刚被选上"的边沿；按实例 ID 索引）。</summary>
	private readonly Dictionary<ulong, bool> _prevBankSel = new Dictionary<ulong, bool>();

	/// <summary>① 当前指针位置（视口坐标；抬起/拖动/移动都会刷新）。</summary>
	private Vector2 _lastPos;
	private bool _lastValid;

	/// <summary>指针此刻是否按着（按下/抬起各刷一次）。</summary>
	private bool _pointerDown;

	/// <summary>② 上一帧"当前拿起的卡"（用于识别"再点同一张"）。</summary>
	private ulong _prevPickId;

	// ── ② 手势簿记（★ v1.2.0）──────────────────────────────────────
	//
	// ★★ 真机日志实证（Android，2026-09-30）：**卡片是在"按下"那一刻就被选中的**，
	//    不是松手时。日志：
	//        指针按下 vp=493,398
	//        ①卡池卡被按下：... 起点=493,398 终点=493,398      ← 按下瞬间就判完了，位移必然 0
	//        指针抬起 vp=583,152                                 ← 其实滑了 200+px
	//    ⇒ 原先"按下→当前"的位移判定**永远算成 0**，① 从不撤销。
	//    同一个事实也把 ② 弄坏了：按下捡起卡 → 同一次点击的**抬起**被我们当成"再点一次"
	//    ⇒ 刚拿起的卡立刻被取消 ⇒ 表现为"单击拿不起卡，只能拖走"（拖走时抬起点不在卡上，才躲过）。
	//
	//    修法：
	//      · ① 选上时只**记下待判定**（起点 + 卡），到**抬起**时才算 起点→抬起点 的真实位移；
	//      · ② 只在"**本手势按下之前**手上就已经拿着这张卡"时才取消 ⇒ 用 `_pressStartPickId`。

	/// <summary>② 本手势"按下那一刻"手上已拿着的卡（0 = 没拿）。只有它才允许被"再点一次"取消。</summary>
	private ulong _pressStartPickId;
	private bool _pressStartValid;
	/// <summary>本手势是否已开始（压掉"触摸→鼠标"重复派发造成的重复按下）。</summary>
	private bool _gestureOpen;
	private ulong _lastPressMs;

	/// <summary>① 刚被选上、等抬起判定位移的卡池卡。</summary>
	private ulong _pendingDragPickId;
	private TowerDefenseInGamePacketShow _pendingDragCard;
	private Vector2 _pendingDragFrom;
	private bool _pendingDragValid;
	private bool _pendingDragIsEditor;
	private ulong _pendingDragDeadlineMs;

	/// <summary>把"重复按下"和"真正的第二次点击"分开的时间窗（毫秒）。</summary>
	private const ulong GestureGapMs = 250;

	/// <summary>① 待判定超时兜底（毫秒）：万一没收到抬起事件，到点用当前位置结案。</summary>
	private const ulong PendingDragTimeoutMs = 2000;

	// ================================================================ 生命周期

	public void Initialize(XWModRuntimeContext context)
	{
		try
		{
			Log("初始化完成（滑动误选撤销 / 误触取消）。");
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
			HookWindowInput();                  // ★ v1.0.3：触摸/鼠标按下位置采样
			_started = true;
			Log("已挂载 process_frame（v1.2.0：①抬起判定位移 / ②只在『按下前已拿着』时才取消）。");
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
			UnhookWindowInput();
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
			FlushPendingPickCancel();                  // ② 帧末取消（在游戏 PickPacket 之后）
			FlushPendingDragPick();                    // ① 待判定超时兜底
			TrackMouseAndPick();                       // ①②共用的逐帧采样
			ScanPacketShows();                         // ①② 状态边沿维护
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
	///
	/// ★★ v1.0.3 变更：①的按下/当前位置**不再轮询鼠标**，改由 `Window.WindowInput`
	///    事件驱动（见 <see cref="OnWindowInputRelay"/>）——
	///    手机上"触摸→鼠标模拟"若未开启，`Input.IsMouseButtonPressed` 永远为 false，
	///    轮询拿不到按下位置 ⇒ 滑动撤销整个失效（实测）。
	///    `Window.WindowInput` 对触摸/鼠标**都**发，且窗口层不受节点 ProcessMode 门控。
	/// </summary>
	private void TrackMouseAndPick()
	{
		try
		{
			// ── ① 按下位置：事件驱动，见 OnWindowInputRelay ─────────

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

	// ================================================================ 窗口输入：按下 / 当前位置采样

	private bool _windowInputHooked;

	/// <summary>
	/// ② 抬起时记录"落点命中哪张卡槽卡"（若它 **在本手势按下之前就已经拿着** ⇒ 下一帧取消）。
	///
	/// ★★ v1.2.0 关键修正：原来只比"上一帧拿起的卡"，结果**同一次点击的抬起**也算命中
	///   （因为卡片是**按下**那一刻就被捡起来的）⇒ 刚拿起的卡立刻被取消，
	///   表现为"单击拿不起卡，只有拖走才有效"（真机实测）。
	///   现在必须满足 `_prevPickId == _pressStartPickId`：即"按下之前手上就已经是它"。
	/// 放在帧末执行的原因：游戏自己的 `OnPressed → PickPacket` 在同一帧的 GUI 派发里跑，
	/// 必须先让它跑完，我们再取消，否则会被它重新拿起。
	/// </summary>
	private void NoteSlotRelease(Vector2 vpPos)
	{
		try
		{
			if (!FixPickCancel || _prevPickId == 0)
			{
				return;
			}
			// ★ 本手势按下之前手上拿的**就是这张**才允许取消；否则这次点它就是"把它捡起来"，不能撤。
			if (!_pressStartValid || _pressStartPickId != _prevPickId)
			{
				return;
			}
			TowerDefenseManager mgr = TowerDefenseManager.Instance;
			if (mgr == null || !GodotObject.IsInstanceValid(mgr))
			{
				return;
			}
			TowerDefenseInGameSeedBank sb = mgr.GetSeedBank();
			if (sb == null || !GodotObject.IsInstanceValid(sb) || sb.hasGameStarted != true)
			{
				return;
			}
			foreach (TowerDefenseInGamePacketShow c in sb.packetList)
			{
				if (c == null || !GodotObject.IsInstanceValid(c) || c.GetInstanceId() != _prevPickId)
				{
					continue;
				}
				// 命中判定：`GetRect()` 含 position、跨坐标系都不可靠 ⇒ 用自身矩形 + 局部鼠标
				Rect2 r = new Rect2(Vector2.Zero, c.Size);
				if (r.HasPoint(c.GetLocalMousePosition()))
				{
					_pendingPickCancel = _prevPickId;
					_pressStartPickId = 0;              // 防"触摸→鼠标"重复抬起再撤一次
					Log("②再点已拿起的卡：" + SafeKey(c) + " ⇒ 下一帧取消选择");
				}
				return;
			}
		}
		catch { }
	}

	/// <summary>② 帧末执行取消（在游戏 `PickPacket` 之后）。</summary>
	private void FlushPendingPickCancel()
	{
		if (_pendingPickCancel == 0)
		{
			return;
		}
		ulong want = _pendingPickCancel;
		_pendingPickCancel = 0;
		try
		{
			PacketPickControl ppc = GetPickControl();
			if (ppc == null || !GodotObject.IsInstanceValid(ppc))
			{
				return;
			}
			TowerDefenseInGamePacketShow pick = ppc.packetPick;
			if (pick != null && GodotObject.IsInstanceValid(pick) && pick.GetInstanceId() == want)
			{
				ppc.PacketPickRelease();
				Log("②已取消误触的植物。");
			}
		}
		catch { }
	}

	/// <summary>② 待取消的卡槽卡实例 ID（0 = 无）。</summary>
	private ulong _pendingPickCancel;

	private void HookWindowInput()
	{
		try
		{
			if (_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return;
			}
			root.WindowInput += OnWindowInputRelay;
			_windowInputHooked = true;
			Log("窗口输入已挂载（WindowInput）—— 触摸/鼠标事件都会经过这里。");
		}
		catch (Exception ex)
		{
			try { GD.PrintErr(P + "挂载窗口输入异常：" + ex.Message); } catch { }
		}
	}

	private void UnhookWindowInput()
	{
		try
		{
			if (!_windowInputHooked)
			{
				return;
			}
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root != null)
			{
				root.WindowInput -= OnWindowInputRelay;
			}
			_windowInputHooked = false;
		}
		catch { }
	}

	/// <summary>窗口坐标 → 视口（内容缩放后）坐标。组件收到的就是这个空间。</summary>
	private Vector2 WindowToViewport(Vector2 windowPos)
	{
		try
		{
			Window root = (_tree != null && GodotObject.IsInstanceValid(_tree)) ? _tree.Root : null;
			if (root == null)
			{
				return windowPos;
			}
			return root.GetFinalTransform().AffineInverse() * windowPos;
		}
		catch
		{
			return windowPos;
		}
	}

	/// <summary>
	/// 窗口级输入采样：触摸/鼠标都走这里。
	/// · 按下（touch pressed / 左键按下）→ 记 `_downPos`（滑动起点）
	/// · 拖动 / 移动 / 抬起   → 记 `_lastPos`（当前点；抬起事件先于卡片 `Pressed` 派发）
	/// ★ `_downValid` 只在下一次按下时刷新，**抬起不清**——
	///   因为卡片 `Pressed` 是在抬起瞬间才派发的，那之后还要用 `_downPos` 算位移。
	/// </summary>
	private int _evCount;

	private void OnWindowInputRelay(InputEvent ev)
	{
		try
		{
			if (ev == null || ev.IsEcho())
			{
				return;
			}
			// ② 抬起 = 判定的时机（游戏 PickPacket 在同一帧 GUI 派发里，之后我们再取消）
			// ① 抬起 = 滑动误选的判定时机（卡片在**按下**时就已选中，必须等抬起才能算真实位移）
			if ((ev is InputEventScreenTouch rt && !rt.Pressed)
				|| (ev is InputEventMouseButton rm && rm.ButtonIndex == MouseButton.Left && !rm.Pressed))
			{
				Vector2 rp = (ev is InputEventScreenTouch t2) ? t2.Position : ((InputEventMouseButton)ev).Position;
				Vector2 vp = WindowToViewport(rp);
				NoteSlotRelease(vp);
				ResolvePendingDragPick(vp);
			}
			_evCount++;
			if (DiagVerbose && (_evCount <= 40 || _evCount % 200 == 0))
			{
				string kind = ev.GetType().Name;
				Vector2 p = Vector2.Zero;
				bool pressed = false;
				if (ev is InputEventScreenTouch st) { p = st.Position; pressed = st.Pressed; }
				else if (ev is InputEventScreenDrag sd) { p = sd.Position; }
				else if (ev is InputEventMouseButton mb) { p = mb.Position; pressed = mb.Pressed; }
				else if (ev is InputEventMouseMotion mm) { p = mm.Position; }
				Log("输入#" + _evCount + " " + kind + " raw=" + (int)p.X + "," + (int)p.Y
					+ " vp=" + (int)WindowToViewport(p).X + "," + (int)WindowToViewport(p).Y
					+ (pressed ? " [按下]" : ""));
			}
			switch (ev)
			{
				case InputEventScreenTouch t:
					ApplyPointer(t.Position, t.Pressed);
					break;
				case InputEventScreenDrag d:
					_lastPos = WindowToViewport(d.Position);
					_lastValid = true;
					break;
				case InputEventMouseButton mb:
					if (mb.ButtonIndex == MouseButton.Left)
					{
						ApplyPointer(mb.Position, mb.Pressed);
					}
					break;
				case InputEventMouseMotion mm:
					_lastPos = WindowToViewport(mm.Position);
					_lastValid = true;
					break;
			}
		}
		catch { }
	}

	private void ApplyPointer(Vector2 windowPos, bool pressed)
	{
		Vector2 p = WindowToViewport(windowPos);
		if (DiagVerbose)
		{
			Log("指针" + (pressed ? "按下" : "抬起") + " vp=" + (int)p.X + "," + (int)p.Y);
		}
		if (pressed)
		{
			// ★ v1.2.0：只在"本手势的第一次按下"时记录"按下之前手上拿的卡"。
			//   手机上同一次触摸会被派发两次（InputEventMouseButton + InputEventScreenTouch），
			//   若第二次按下也重记，就可能把"刚被这次点击捡起来的那张"记进去 ⇒ ② 会误取消。
			ulong now = Time.GetTicksMsec();
			if (!_gestureOpen || (now - _lastPressMs) > GestureGapMs)
			{
				_pressStartPickId = _prevPickId;
				_pressStartValid = true;
				_gestureOpen = true;
			}
			_lastPressMs = now;
			_pointerDown = true;
			_downPos = p;
			_downValid = true;
			_lastPos = p;
			_lastValid = true;
		}
		else
		{
			_pointerDown = false;
			_gestureOpen = false;
			_lastPos = p;
			_lastValid = true;
		}
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

			var cards = new List<TowerDefenseInGamePacketShow>();
			CollectByType(_tree != null && GodotObject.IsInstanceValid(_tree) ? _tree.Root : null,
				0, cards, 500);

			// ★ 收集"目标容器成员"集合，容器外的同名卡清掉钩子标记
			//   （卡走对象池，ResetForPool → ClearEventHandlers 会把我们挂的事件清掉，
			//     若标记还留着就永远不会重挂 ⇒ 功能静默失效）。
			var bankMembers = new HashSet<ulong>();
			var slotMembers = new HashSet<ulong>();
			var editorMembers = new HashSet<ulong>();

			foreach (TowerDefenseInGamePacketShow c in cards)
			{
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				ulong id = c.GetInstanceId();

				// ── ① 选卡界面卡池的卡 ──────────────────────────────
				// ★★ v1.0.6 关键修复：**不再依赖 `OnPressed` 事件钩子**！
				//   卡池是**虚拟化**的：卡滑出视野会 `ResetForPool()` → `ClearEventHandlers()`
				//   **把我们挂的事件清空**，而那张卡此时已不在树里、我们的"清标记"扫不到它
				//   ⇒ 复用回视野时 `HasMeta` 还是 true ⇒ 跳过挂钩 ⇒ **那张卡永远没有钩子**
				//   （实测日志：关卡内滑了 4 次，`①卡池卡被按下` 一条都没有）。
				//   ⇒ 改成**逐帧看 `select` 的 false→true 边沿** + 按下/抬起位移判定：
				//     游戏 `Pressed()` 里就是 `select = !select`，选上那一刻必定是边沿 ✓
				//     与对象池、与事件订阅完全无关。
				if (FixScrollMisPick && IsInChooseBank(c))
				{
					bankMembers.Add(id);
					bool curSel = false;
					try { curSel = c.select; } catch { }
					bool hadSel = _prevBankSel.TryGetValue(id, out bool prevSel);
					_prevBankSel[id] = curSel;
					if (inChoose && hadSel && curSel && !prevSel)
					{
						MarkPendingDragPick(c, isEditor: false);
					}
				}
				// ── ② 战斗卡槽的卡 ──────────────────────────────────
				//   ★ v1.0.6：同样**不再依赖事件钩子**（卡槽卡也走对象池，`ResetForPool`
				//   会把钩子清掉而标记留在节点上 ⇒ 复用后不再挂钩）。
				//   改为：**按下/抬起事件里记下"落点命中哪张卡槽卡"**，帧末再决定是否取消
				//   （必须在游戏自己的 `PickPacket` 派发之后，否则会被它重新拿起）。
				// ── ①b 自制关卡（关卡编辑器）的编辑卡牌库 ────────────
				//   ★ v1.0.7：那一套卡池是另一个类 `LevelEditorPacketBank`，
				//   同一个"滑一下就误选"的问题也在 —— 撤销方式是**取消拿起**
				//   （那里点卡 = `PickPacket` 拿起准备摆放）。
				else if (FixScrollMisPick && IsInEditorBank(c))
				{
					editorMembers.Add(id);
					bool curE = false;
					try { curE = c.select; } catch { }
					bool hadE = _prevBankSel.TryGetValue(id, out bool prevE);
					_prevBankSel[id] = curE;
					if (hadE && curE && !prevE)
					{
						MarkPendingDragPick(c, isEditor: true);
					}
				}
				else if (FixPickCancel && inChoose == false && seedBank != null
					&& GodotObject.IsInstanceValid(seedBank) && InList(seedBank.packetList, c))
				{
					slotMembers.Add(id);
				}
			}

			// 容器外的同名卡：清标记（下次进容器会重新挂；此刻事件已被对象池清空）
			foreach (TowerDefenseInGamePacketShow c in cards)
			{
				if (c == null || !GodotObject.IsInstanceValid(c))
				{
					continue;
				}
				ulong id = c.GetInstanceId();
				if (!bankMembers.Contains(id) && !editorMembers.Contains(id))
				{
					_prevBankSel.Remove(id);       // 卡离开卡池 ⇒ 清掉它的选中状态记录
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

	/// <summary>
	/// ① ★★ v1.2.0：卡池卡"刚被选上"时**只登记**，不在这里判定。
	///
	/// 为什么不在这里判：真机日志实证 —— **卡片是在"按下"那一刻就被选中的**：
	///     指针按下 vp=493,398
	///     ①卡池卡被按下：… 起点=493,398 终点=493,398        ← 按下瞬间就判完了，位移必然 0
	///     指针抬起 vp=583,152                                 ← 其实滑了 200+px
	/// 此刻当前位置 == 按下位置 ⇒ 位移恒为 0 ⇒ ① 从不撤销。
	/// ⇒ 改成**抬起时**用 `起点 → 抬起点` 的真实位移判定（见 <see cref="ResolvePendingDragPick"/>）。
	/// 若登记时指针**已经松开**（万一游戏是松手时才选中），就立刻结案 —— 两种时序都能覆盖。
	/// </summary>
	private void MarkPendingDragPick(TowerDefenseInGamePacketShow card, bool isEditor)
	{
		try
		{
			if (card == null || !GodotObject.IsInstanceValid(card))
			{
				return;
			}
			_pendingDragPickId = card.GetInstanceId();
			_pendingDragCard = card;
			_pendingDragIsEditor = isEditor;
			_pendingDragFrom = _downValid ? _downPos : (_lastValid ? _lastPos : GetMousePos());
			_pendingDragValid = true;
			_pendingDragDeadlineMs = Time.GetTicksMsec() + PendingDragTimeoutMs;
			Log("①" + (isEditor ? "自制关卡" : "选卡界面") + "卡池卡刚被选上（等抬起判定位移）："
				+ SafeKey(card) + " 起点=" + (int)_pendingDragFrom.X + "," + (int)_pendingDragFrom.Y);
			if (!_pointerDown)
			{
				ResolvePendingDragPick(_lastValid ? _lastPos : _pendingDragFrom);
			}
		}
		catch { }
	}

	/// <summary>① ★★ v1.2.0：抬起（或超时）时用"起点 → 抬起点"的真实位移判定是否撤销误选。</summary>
	private void ResolvePendingDragPick(Vector2 vpPos)
	{
		if (!_pendingDragValid)
		{
			return;
		}
		ulong id = _pendingDragPickId;
		TowerDefenseInGamePacketShow card = _pendingDragCard;
		bool isEditor = _pendingDragIsEditor;
		Vector2 from = _pendingDragFrom;
		_pendingDragValid = false;
		_pendingDragPickId = 0;
		_pendingDragCard = null;
		try
		{
			float dist = (vpPos - from).Length();
			Log("①抬起判定位移=" + (int)dist + "px（阈值 " + (int)DragThresholdPx + "）起点="
				+ (int)from.X + "," + (int)from.Y + " 终点=" + (int)vpPos.X + "," + (int)vpPos.Y);
			if (dist < DragThresholdPx)
			{
				return;                        // 正常点击，不干预
			}
			if (card == null || !GodotObject.IsInstanceValid(card) || card.GetInstanceId() != id)
			{
				return;                        // 卡已被回收 ⇒ 没什么可撤
			}
			bool sel = false;
			try { sel = card.select; } catch { }
			if (!sel)
			{
				Log("①该卡已不是选中态，无需撤销。");
				return;
			}
			if (isEditor)
			{
				CancelEditorPick(card);        // 自制关卡：撤销 = 取消拿起 + `Reset()`
				return;
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
			Log("①滑动误选已撤销（位移 " + (int)dist + "px）：" + SafeKey(card));
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

	/// <summary>① 超时兜底：万一起抬手势没收到抬起事件（丢事件 / 被系统抢走），到点结案。</summary>
	private void FlushPendingDragPick()
	{
		if (!_pendingDragValid)
		{
			return;
		}
		if (Time.GetTicksMsec() < _pendingDragDeadlineMs)
		{
			return;
		}
		ResolvePendingDragPick(_lastValid ? _lastPos : _pendingDragFrom);
	}

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

	/// <summary>这张卡是否挂在**自制关卡的编辑卡牌库**（`LevelEditorPacketBank`）之下。</summary>
	private static bool IsInEditorBank(Node node)
	{
		try
		{
			for (Node p = node?.GetParent(); p != null; p = p.GetParent())
			{
				if (p is LevelEditorPacketBank)
				{
					return true;
				}
			}
		}
		catch { }
		return false;
	}

	/// <summary>
	/// ①b 自制关卡：撤销"滑动误选"。那里点卡 = `LevelEditorPacketBank.PacketChoose()`
	/// ⇒ `packetPickControl.PickPacket(packet)`（把卡"拿起来"准备往地图上摆）。
	/// 撤销 = `PacketPickRelease()`（放下）+ 把卡面 `Reset()`（清掉已选高亮）。
	/// </summary>
	private void CancelEditorPick(TowerDefenseInGamePacketShow card)
	{
		try
		{
			LevelEditorPacketBank eb = LevelEditorPacketBank.Instance;
			if (eb == null || !GodotObject.IsInstanceValid(eb))
			{
				return;
			}
			TowerDefenseBattleFeatureMap mf = eb.mapFeature;
			if ((mf == null || !GodotObject.IsInstanceValid(mf))
				&& LevelEditorMapEditor.instance != null
				&& GodotObject.IsInstanceValid(LevelEditorMapEditor.instance))
			{
				mf = LevelEditorMapEditor.instance.mapFeature;
			}
			if (mf != null && GodotObject.IsInstanceValid(mf)
				&& mf.packetPickControl != null && GodotObject.IsInstanceValid(mf.packetPickControl))
			{
				mf.packetPickControl.PacketPickRelease();
			}
			if (card != null && GodotObject.IsInstanceValid(card))
			{
				try { card.Reset(); } catch { }
			}
			Log("①自制关卡：滑动误选已撤销（" + SafeKey(card) + "）");
		}
		catch (Exception ex)
		{
			if (_diag < 107)
			{
				_diag = 107;
				Log("自制关卡撤销异常（本条只报一次）：" + ex.Message);
			}
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

	/// <summary>
	/// 按**类型**递归收集节点（深度优先，最多 max 个）。
	///
	/// ★★ v1.0.3 关键修复：`is T` 判定天然覆盖子类 ——
	///   `CollectByClassName` 用 `GetType().Name == 类名` **精确相等**，
	///   而关卡/章节入口的实际类型是 `DragMenuSelectItemlevel` / `DragMenuSelectItemChapter`
	///   （都是 `DragMenuSelectItem` 的**子类**）⇒ 永远匹配不上 ⇒ 按压反馈整个没生效（实测）。
	/// </summary>
	private static void CollectByType<T>(Node node, int depth, List<T> outList, int max) where T : class
	{
		try
		{
			if (node == null || !GodotObject.IsInstanceValid(node) || outList.Count >= max || depth > 40)
			{
				return;
			}
			if (node is T t)
			{
				outList.Add(t);
			}
			int n = node.GetChildCount();
			for (int i = 0; i < n; i++)
			{
				CollectByType(node.GetChild(i), depth + 1, outList, max);
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
