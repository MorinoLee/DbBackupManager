using System.ComponentModel.DataAnnotations;

namespace DbBackupManager.Web.Components.Pages;

/// <summary>
/// 服务器与实例编辑弹窗使用的表单模型；保存时映射回 Application 输入记录。
/// </summary>
public sealed class ServerFormModel
{
    [Required(ErrorMessage = "请输入服务器名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入 SQL 本地备份目录。"), StringLength(2048)]
    public string LocalPath { get; set; } = string.Empty;

    public int Protocol { get; set; } = 1;

    [Required(ErrorMessage = "请输入暂存访问主机。"), StringLength(255)]
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    [Required(ErrorMessage = "请输入暂存访问根路径。"), StringLength(2048)]
    public string BasePath { get; set; } = string.Empty;

    public Guid CredentialId { get; set; }

    public string Fingerprint { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

public sealed class InstanceFormModel
{
    public Guid ServerId { get; set; }

    [Required(ErrorMessage = "请输入实例显示名称。"), StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "请输入 SQL 连接地址。"), StringLength(255)]
    public string Address { get; set; } = string.Empty;

    public Guid CredentialId { get; set; }

    public bool AllowLegacyTls { get; set; }

    public string LegacyTlsReason { get; set; } = string.Empty;

    public bool Trust { get; set; }

    public string Reason { get; set; } = string.Empty;

    public int Timeout { get; set; } = 15;

    public bool Enabled { get; set; } = true;
}
