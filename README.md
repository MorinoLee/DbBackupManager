# DbBackupManager

面向内部 IT 与 SQL Server DBA 的备份管理工具，提供中文 Web 管理界面和独立后台 Worker。使用 .NET 10、Blazor Interactive Server、MudBlazor 与 EF Core。

本仓库提供源码、自动化测试和自行构建部署的脚本；当前不提供官方二进制安装包。适用于单 Web、单活动 Worker 的 Windows 环境。部署前请阅读[兼容与已知限制](#兼容与已知限制)。

## 主要功能

- 管理 SQL 凭据、服务器、实例与数据库发现；密码及私钥保存后不回显。
- 每个已纳管数据库一个当前备份计划，可配置完整备份或完整备份加差异备份，CHECKSUM 默认开启。使用中的计划由单个 Worker 在后台按时间规则排队并串行执行；差异备份内置放行 SQL Server 2016（产品主次版本 `13.0`，实测边界 13.0.5026.0、Windows 服务身份、SMB），其他版本默认拒绝。定时删除和日志备份仍未启用。
- 手动与既有 FULL 备份、`RESTORE VERIFYONLY` 校验、任务状态及历史查询。任务和文件页展示备份类型、计划版本、执行事实和保留原因。
- 仅本地、本地加远程、仅远程三种存储模式，支持 SMB、SFTP 密码与私钥认证。
- 分层备份目录、远程临时文件与校验、已登记副本的保留清理。
- SMTP 通知覆盖统一计划的明确失败、结果未知和远端暂时无法恢复。Worker 心跳、工作台和任务状态实时刷新仍然保留。
- Windows Service 安装、升级、失败回退与平台数据/密钥恢复步骤。
- 同主机 `/api/v1` API，使用 Cookie 与 CSRF；[OpenAPI 契约](src/DbBackupManager.Web/OpenApi/v1.json)随源码提供。

## 快速开始

准备 Windows x64、PowerShell 7、`global.json` 指定的 .NET 10 SDK，以及独立的 SQL Server 2019 或更高版本平台数据库。运行测试还需 SQL Server LocalDB。

```powershell
git clone https://github.com/MorinoLee/DbBackupManager.git
cd DbBackupManager
dotnet restore DbBackupManager.slnx
dotnet build DbBackupManager.slnx --configuration Release --no-restore
```

按[开发运行](docs/开发运行.md)配置平台连接、两套密钥目录并手动应用 Migration，启动 Web 完成首次管理员设置和业务凭据录入，再启动 Worker。仓库中的配置占位符必须替换为部署者自己的值，真实配置不能提交。

## 文档

| 文档 | 内容 |
|---|---|
| [开发运行](docs/开发运行.md) | 环境、配置、启动、测试与 API 开发 |
| [使用指南](docs/使用指南.md) | 凭据、数据库、备份计划、任务、存储和通知 |
| [部署与运维](docs/部署与运维.md) | 构建版本目录、Windows Service、权限、升级和恢复 |
| [架构说明](docs/架构说明.md) | 分层、数据访问、任务安全与实时通知 |

## 兼容与已知限制

本仓库公开当前源码快照，不提供官方安装包或全面生产兼容认证。以下列出已测范围和验证限制；部署者仍需核对自己的操作系统、身份、网络和存储组合。

| 范围 | 已有证据与边界 |
|---|---|
| 运行宿主 | Windows x64、单 Web 和单活动 Worker，Windows Service 新装、升级失败回退、启停和跨机平台恢复已有实测。Windows 11 build 26200 与测试机 build 19045.6033 属已测组合；正式 Windows Server 未实测，不据此承诺其可用性或厂商支持 |
| 平台数据库 | SQL Server 2019 与 SQL Server 2022 有实测；模型兼容级别 150。平台连接成功不等于具有迁移或全部业务权限 |
| 目标数据库 | SQL Server 2008 R2 RTM 10.50.1600.1 与 SQL Server 2016 SP2 13.0.5026.0 有受测证据；2016 的差异备份实测边界为 Windows 服务身份与 SMB，覆盖 FULL/DIFF、远端恢复集清理后同版本还原及副本闭环。内置 DIFF 放行只登记主次版本 `13.0`，其他版本仍默认拒绝；不代表任意补丁、身份、协议、认证和 TLS 组合均受支持 |
| 当前驱动 | SqlClient 7.0.3 已通过自动化测试，以及隔离环境中的 Web/Worker 运行、目标备份和 SMB 双副本验证；较早版本的其他实测结果不代表当前驱动已验证 |
| 旧版 SQL | 2008 R2 仅保留项目级兼容，现代驱动官方支持矩阵不覆盖。RTM/TLS 1.0 受测场景需显式例外；该 SQL 版本的完整还原，以及当前 Worker 在该版本下的故障恢复场景尚未全部验证 |
| 文件协议 | Worker 的 SMB、SFTP 密码及带口令私钥传输成功场景已有实测，SFTP 固定 Host Key；SqlClient 7.0.3 下重点复验了 SMB，尚未覆盖所有协议的故障组合 |
| 访问范围 | 既有部署验收只证明回环访问；Production 使用 HTTP，内网部署须核对可信来源、防火墙与网络 ACL。禁止直接暴露公网 |
| 恢复 | 平台数据库、配置和两套密钥的组合恢复有实测；业务备份的 VERIFYONLY 不等于实际恢复。跨版本还原曾需校正新库目录计数，再完成 CHECKDB；不能概括为任意备份无条件可恢复 |

### 明确限制

- 单个 Worker 串行调度和执行，一轮至多一个任务。结果未知只做只读核对，不重发 BACKUP；租约到期不表示 SQL 已停止。原调用已终止但成功证据不足时，以 `ClosedUnknown` 收口并保留证据与文件；没有有效终止证据时继续阻塞。
- DIFF 必须依赖已核对的受管普通 FULL。首次启用、版本改为含 DIFF 或从暂停恢复时，满足条件才补基线；活动基线未知、未受管或不一致时不自动补，也不把 DIFF 改成 FULL。临时 COPY_ONLY FULL 不作为 DIFF 基线。配置与补基线行为见[备份计划](docs/使用指南.md#备份计划)。
- 计划后台循环已开放；LOG、定时删除和依赖删除定时器仍关闭。RemoteOnly 的只读核对不删文件，需要时由执行路径继续原 Attempt 的清理。租约、核对、恢复和删除约束见[执行安全](docs/架构说明.md#执行安全)。
- 明确失败、结果未知和远端暂时无法恢复使用邮件 Outbox；主题、去重及恢复条件见[通知与工作台](docs/使用指南.md#通知工作台与权限)。多节点和复杂账号权限管理不在当前范围。
- 浏览器断线重连、Windows 服务账号的保留清理删除权限、计划执行期间的资源占用，以及停机错过多个计划时隙后的任务补建，尚未全部完成真实环境验证；已有相关自动化检查。
- 未建立吞吐、容量、长期负载等数字承诺。
- SQL 就绪探针只证明平台连接；不代表 Migration、业务权限、Worker 心跳、目标 SQL 和存储都正常。
- 共享运行时、服务身份、权限、配置、目标版本或依赖发生变化后，部署者应验证受影响场景。

### 兼容例外

SQL 证书信任、旧版 SQL/TLS 和 SMTP 明文均不是默认安全基线，必须由管理员显式配置并登记原因。仅用于能够接受这些限制的隔离或受控网络，不能据此关闭其他授权、Host Key、路径或秘密保护措施。

首次部署先以专用数据库验证备份、文件校验和隔离还原，再启用业务计划。恢复后的平台可能引用原目标；启动恢复 Worker 前须核对计划、源、存储、SMTP 和 Retention 的实际访问范围。仅停用计划不能阻止所有后台 I/O。

## 参与和安全

开发规则见[贡献指南](CONTRIBUTING.md)。安全问题请按[安全报告](SECURITY.md)私下反馈。平台数据库和备份目标数据库职责不同；不要把应用直接暴露到公网，部署者须保护连接配置与密钥并验证自己的恢复流程。

## 许可证

项目自有代码采用 [MIT](LICENSE)。第三方组件保留各自许可，补充材料位于 [third-party](third-party/Google.MaterialDesignIcons/NOTICE.md)。自行构建的二进制涉及独立第三方条件，见[二进制第三方条款](DISTRIBUTION-TERMS.md)；源码公开不代表本项目已批准二进制再分发。
