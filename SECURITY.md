# 安全报告

请优先通过仓库 [Security → Report a vulnerability](https://github.com/MorinoLee/DbBackupManager/security/advisories/new) 私下报告漏洞。如果入口不可用，可在 Issue 中提交不含漏洞细节的联系请求，由维护者安排私密渠道；不要公开可利用细节。

报告请提供受影响提交、脱敏环境、最小复现步骤和影响。不要附带密码、私钥、Cookie、完整连接字符串、真实服务器地址、备份文件或数据库副本。

本仓库提供源码，目前没有官方二进制发行版或固定响应时限。维护工作围绕最新 `main` 展开，兼容范围见[兼容与已知限制](README.md#兼容与已知限制)。

部署使用者须保护平台数据库、Cookie/业务 Key Ring 和外部配置。Production HTTP、旧 SQL/TLS 和明文 SMTP 仅属于显式配置的受控例外；禁止直接暴露在公网。操作要求见[部署与运维](docs/部署与运维.md)。
