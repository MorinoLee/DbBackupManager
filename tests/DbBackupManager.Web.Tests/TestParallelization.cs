using Xunit;

// 该项目的主机测试各自创建 LocalDB 库并执行完整 Migration；并行运行会争用同一实例并导致超时或拆库失败。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
