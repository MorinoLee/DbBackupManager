# 贡献指南

项目使用 [MIT 许可证](LICENSE)，请只贡献有权按该许可提供的内容，并保留第三方来源与许可。安全问题见 [SECURITY.md](SECURITY.md)。

阅读 [AGENTS.md](AGENTS.md)、[架构说明](docs/架构说明.md)和[开发运行](docs/开发运行.md)，从 `main` 建立分支。提交前执行：

```powershell
dotnet restore DbBackupManager.slnx
dotnet format DbBackupManager.slnx --verify-no-changes --no-restore
dotnet build DbBackupManager.slnx --configuration Release --no-restore
dotnet test DbBackupManager.slnx --configuration Release --no-build --filter 'Category!=P56TargetSqlAcceptance'
pwsh -File scripts/ci/Check-Docs.ps1
git diff --check
```

以上测试需要 Windows LocalDB，不执行专用外部目标 SQL 验收。部署脚本修改还需执行[开发运行](docs/开发运行.md)中的对应检查。真实目标写入不得作为默认测试前提。

提交使用 `fix(worker): 修复任务租约恢复` 等中文 Conventional Commit。PR 说明具体问题、行为变化、验证及限制；涉及 API、数据库、配置、权限或数据删除时说明影响。截图使用合成数据，不上传真实备份、配置或日志原文。
