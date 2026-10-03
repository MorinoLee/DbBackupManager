# 二进制包的第三方条款

本文件适用于包含 `Microsoft.Data.SqlClient.SNI.runtime/6.0.3` 的 DbBackupManager 二进制包。DbBackupManager 自有源码继续使用项目 [MIT 许可证](LICENSE)；本文件只规定随包 Microsoft SNI 组件的使用与再分发条件，不重新许可项目源码或其他组件。

## 接受完整原条款

安装、复制、使用或再分发包中的 Microsoft SNI 组件前，外部最终使用者和分发者须阅读并同意随包的 **MICROSOFT SOFTWARE LICENSE TERMS — MICROSOFT.DATA.SQLCLIENT.SNI LIBRARY** 完整原文。你同意就该组件遵守并受这些原条款约束；本文件的中文说明不取代、删减或扩大原文授权。

完整原文位于包内 `licenses/Microsoft.Data.SqlClient.SNI.runtime/6.0.3/DECLARED-LICENSE.txt`，官方来源为 [Microsoft NuGet 包](https://www.nuget.org/packages/Microsoft.Data.SqlClient.SNI.runtime/6.0.3)。其余第三方原许可、版权和 NOTICE 见 `THIRD-PARTY-NOTICES.txt` 与 `licenses/index.json`。

## 使用与再分发

- SNI 目标代码随本应用提供，不作为独立产品分发；不得删除或修改原许可、版权及第三方声明。
- SNI 原文中的使用范围、技术限制、商标、出口、数据、保证与责任条款均适用于该组件；项目 MIT 不提供对 SNI 的额外授权。
- 再分发本应用时，应完整转交本文件及所有适用的原许可和 NOTICE，要求外部最终使用者及后续分发者同意对该组件至少提供同等保护的条款，并自行满足 SNI 原文中的分发条件与责任。
- 不同意这些条款时，不安装、使用或再分发随包的 SNI 组件。

## 安装时的接受

部署脚本的 Plan 会显示本文件的包内路径及接受状态；Install / Upgrade 的 Apply 必须显式传入 `-AcceptDistributionTerms`。该参数表示执行者已阅读上述完整原文，并有权代表安装或使用主体接受这些条款。它不能代替发布方的分发责任确认，也不能把未通过分发门禁的验收包变成正式分发包。

通过脚本以外的方式安装、使用或再分发时，同样应先完成上述阅读与接受；原许可的适用不依赖使用哪一个安装工具。
