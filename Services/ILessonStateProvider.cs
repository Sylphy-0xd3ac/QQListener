namespace QQListener.Services;

/// <summary>
/// 当前是不是正在上课。
///
/// 通过独立接口读取宿主课表，让积压与课后延迟逻辑可以离线验证。
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
