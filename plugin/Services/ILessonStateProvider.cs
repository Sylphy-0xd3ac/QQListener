namespace QQListener.Services;

/// <summary>
/// 当前是不是正在上课。
///
/// 单独抽一层而不是直接在规则引擎里拿 ClassIsland 的 <c>ILessonsService</c>：规则引擎
/// 是纯逻辑、离线可测的，直接依赖宿主服务会把整套规则测试拖进 ClassIsland 的运行时。
/// </summary>
public interface ILessonStateProvider
{
    /// <summary>正在上课（不含预备铃、课间与放学后）。</summary>
    bool IsOnClass { get; }
}

/// <summary>拿不到课表时的兜底：永远当作"不在上课"，也就是照常提醒。
/// 宁可多弹一条，也不要因为课表服务缺失就把消息全静音。</summary>
public sealed class AlwaysOffClassProvider : ILessonStateProvider
{
    public bool IsOnClass => false;
}
