namespace QQListener.Core;

public enum CoreRuntimeState { Unsupported, Detached, Paused, NoQq, Waiting, Connected, Error }

public readonly record struct CoreRuntimeSnapshot(CoreRuntimeState State, string Detail = "", int Pid = 0);

/// <summary>
/// 核心装载/捕获的实时状态快照，供诊断页与设置页展示。
///
/// 跟 <see cref="CoreController"/> 的目标状态是两回事：目标状态是"用户想要什么"，
/// 这里是"实际发生了什么"（有没有找到 QQ、映像有没有映射成功、管道有没有连上）。
/// </summary>
public static class CoreRuntime
{
    private static readonly object Gate = new();
    private static CoreRuntimeSnapshot _snapshot = new(CoreRuntimeState.Waiting, "等待核心服务启动");

    public static CoreRuntimeSnapshot Get()
    {
        lock (Gate) return _snapshot;
    }

    public static void Set(CoreRuntimeState state, string detail = "", int pid = 0)
    {
        lock (Gate) _snapshot = new CoreRuntimeSnapshot(state, detail, pid);
    }

    /// <summary>重置回启动前的默认快照：测试用它隔离用例；
    /// <see cref="Services.QqEngineService"/> 停止插件时也要调用，
    /// 理由跟 <see cref="CoreController.ResetToDefault"/> 一致——这是进程级静态状态。</summary>
    internal static void ResetToDefault() => Set(CoreRuntimeState.Waiting, "等待核心服务启动");
}
