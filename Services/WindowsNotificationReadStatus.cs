namespace QQListener.Services;

/// <summary>
/// 一次轮询的粗粒度结果。名字带着"WindowsNotificationRead"的历史包袱——这个类型
/// 原本是旧 toast 数据源专用的，旧数据源整套已经删除，但 <see cref="Models.QqNotificationPollResult"/>
/// 仍然共用它作为数据源无关的成功/失败信号（原生数据源恒报 Success，失败重试节奏
/// 靠 <see cref="Models.QqNotificationPollResult.IsSuccess"/> 判定）。改名成本大于收益，先留着。
/// </summary>
public enum WindowsNotificationReadStatus
{
    Success,
    PermissionDenied,
    ListenerUnavailable,
    ReadFailed
}
