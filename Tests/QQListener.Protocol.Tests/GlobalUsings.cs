global using Xunit;

// QQListener.Core.CoreController / CoreRuntime 是进程内全局单例状态；
// 关掉并行避免不同测试类互相踩状态。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
