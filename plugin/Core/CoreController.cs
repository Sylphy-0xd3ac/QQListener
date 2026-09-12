namespace QQListener.Core;

public enum CoreState { Detached, Running, Paused }

/// <summary>
/// 核心（注入 + 捕获）运行状态机。
///
/// 三态：
/// - Detached  未注入：钩子不在 QQ 里，没有管道。
/// - Running   运行中：钩子已注入且捕获连通。
/// - Paused    已暂停：钩子仍在 QQ 里，但停止读管道（轻量、可秒恢复）。
///
/// 交互约定（由 UI 层触发）：
/// - 单击 → <see cref="Toggle"/>：Running⇄Paused；Detached→Running（重新启动）。
/// - 长按 → <see cref="Unload"/>（需二次确认）：任意态 → Detached。
///
/// 本类只管状态与监听器；真正的注入/卸载/连管道副作用由订阅者
/// （<see cref="CoreService"/>）在收到状态变化时执行。对照 Python 版
/// <c>src/core/core_controller.py</c>。
///
/// 监听器回调不保证按状态变化发生的顺序收到：两次并发的 <see cref="Transition"/>
/// 各自在锁内确定要通知的目标状态，但实际调用监听器发生在锁外，两次通知谁先
/// 执行不确定。监听器不能假设"收到的最后一次通知 = 当前真实状态"，需要的话应该
/// 回读 <see cref="GetState"/> 而不是依赖回调参数的先后顺序。
/// </summary>
public static class CoreController
{
    private static readonly object Gate = new();
    private static CoreState _state = CoreState.Running;
    private static readonly List<Action<CoreState>> Listeners = [];

    public static CoreState GetState()
    {
        lock (Gate) return _state;
    }

    public static void SetState(CoreState state) => Transition(_ => state);

    /// <summary>单击：运行⇄暂停；未注入→运行。返回新状态。</summary>
    public static CoreState Toggle() =>
        Transition(current => current == CoreState.Running ? CoreState.Paused : CoreState.Running);

    /// <summary>长按（已二次确认）：卸载核心 → 未注入。返回新状态。</summary>
    public static CoreState Unload() => Transition(_ => CoreState.Detached);

    /// <summary>
    /// "读当前态 → 算目标态 → 写入 → 通知"整个流程收在同一把锁里完成。
    ///
    /// 之前 Toggle/Unload 是 GetState() + SetState(...) + GetState() 三次独立取锁：
    /// 两个线程并发 Toggle 可能都读到 Running、都写 Paused（其中一次因为状态没变
    /// 被 SetState 的相等判断吞掉），净效果是"点了两次只翻转了一次"；返回值也
    /// 可能不是本次调用真正设置的那个状态，而是别的线程随后又改过的值。
    /// </summary>
    private static CoreState Transition(Func<CoreState, CoreState> next)
    {
        CoreState newState;
        Action<CoreState>[] listeners;
        lock (Gate)
        {
            newState = next(_state);
            if (newState == _state) return _state;
            _state = newState;
            listeners = [.. Listeners];
        }
        foreach (var listener in listeners) listener(newState);
        return newState;
    }

    public static bool IsRunning() => GetState() == CoreState.Running;

    public static void AddListener(Action<CoreState> listener)
    {
        lock (Gate)
        {
            if (!Listeners.Contains(listener)) Listeners.Add(listener);
        }
    }

    public static void RemoveListener(Action<CoreState> listener)
    {
        lock (Gate) Listeners.Remove(listener);
    }

    /// <summary>
    /// 把全局状态重置回默认值：测试用它隔离用例；生产代码里
    /// <see cref="Services.NativeCoreLifecycleService.StopAsync"/> 在插件停止时
    /// 也要调用——这是进程级静态状态，如果 ClassIsland 允许"禁用插件再重新启用"
    /// 而不重启整个宿主进程，上一轮残留的 Detached/Paused 状态和监听器列表会
    /// 原样带进下一轮，新的 CoreService 首次 Reconcile 就会读到一个跟这次启动
    /// 完全无关的旧状态（比如直接判定 Detached、放弃注入），而不是清清爽爽的默认值。
    /// </summary>
    internal static void ResetToDefault()
    {
        lock (Gate)
        {
            _state = CoreState.Running;
            Listeners.Clear();
        }
    }
}
