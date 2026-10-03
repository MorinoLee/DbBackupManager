using Xunit;

// 该项目的测试共用本机 LocalDB；并行建库、迁移与拆库会争用同一实例并导致超时或“数据库正在使用”。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
