using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace QQListener.Services;

/// <summary>
/// 从 ClassIsland 的 <see cref="ILessonsService"/> 读当前时间状态。
///
/// 服务是延迟解析的：插件的 <c>Initialize</c> 在宿主容器建好之前就跑完了，构造时去拿
/// <c>ILessonsService</c> 会拿不到；而且宿主重载插件时容器会换，缓存住一个实例反而会
/// 读到已经 dispose 的旧服务。
/// </summary>
public sealed class ClassIslandLessonStateProvider : ILessonStateProvider
{
    public bool IsOnClass
    {
        get
        {
            try
            {
                var lessons = IAppHost.Host?.Services.GetService<ILessonsService>();
                // 只认"正在上课"。预备铃和课间还没真的开始上课，放学后更不该静音。
                return lessons?.CurrentState == TimeState.OnClass;
            }
            catch (Exception)
            {
                // 容器正在销毁 / 课表服务未就绪：当作不在上课，照常提醒。
                return false;
            }
        }
    }
}
