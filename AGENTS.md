# 仓库协作规则

- 先读 README 和本次涉及的文档，再检查 Git 状态；保留他人的未提交改动。
- 分层与安全约束见 [架构说明](docs/架构说明.md)。Web/API 共用 Application 用例，Worker 独立；Platform DB 只通过 `IDbContextFactory` 使用短生命周期 Context，外部 I/O 不持有 Context 或事务。
- 更改认证、删除/Retention、结果不确定时的重试或目标 SQL 支持范围前，先与维护者确认。
- 不提交凭据、密钥、完整连接字符串、真实服务器地址、共享路径或业务数据。测试使用合成值；真实外部写入须先确认精确目标和范围。
- 代码变更运行受影响测试；公开 API 变更同步 OpenAPI，数据库变更同步 Migration。维护与行为相关的用户文档，不新增内部任务流水。
- 纯文档修改运行 `scripts/ci/Check-Docs.ps1` 和 `git diff --check`。自动化测试不能代替真实环境验收。
- 使用独立分支和 Pull Request；提交采用中文 Conventional Commit。只暂存本次文件，不丢弃未知改动或强制推送共享分支。
- 自编文档与用户可见文字使用中文，第三方许可原文保持原样。
