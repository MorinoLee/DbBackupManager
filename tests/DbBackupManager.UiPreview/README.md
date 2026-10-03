# 隔离浏览器验收预览

从仓库根目录执行，需要已安装并可运行的 MSSQLLocalDB：

```powershell
SqlLocalDB start MSSQLLocalDB
dotnet run --project tests/DbBackupManager.UiPreview --configuration Release -- --seed
```

浏览器打开 http://localhost:5088/login 。演示账号在 Preview.cs 的初始化请求中定义，仅用于每次随机创建的临时库。省略 `--seed` 可检查空工作台；在输出的 CONTROL 目录创建名为 `seed` 的空文件可填充演示数据。

在 CONTROL 目录创建名为 `stop` 的空文件正常结束，或等待两小时自动结束。正常退出删除该进程随机创建的数据库与临时密钥目录；强制杀进程可能无法执行清理，此时只处理该次输出对应资源，不能按前缀批量删除。

项目不加入 Solution，作为手动验收工具独立构建。清空配置来源，只使用随机 LocalDB 与临时密钥；SQL 探测为合成实现，不启动 Worker，不访问演示主机或共享目录，不执行实际备份。截图前等待数据加载及交互初始化完成。
