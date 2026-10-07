namespace DbBackupManager.Domain.BackupSets;

public readonly record struct BackupLsn
{
    public const decimal MaximumValue = 9999999999999999999999999m;

    public BackupLsn(decimal value)
    {
        if (value < 0 || value > MaximumValue || decimal.Truncate(value) != value)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "LSN 必须是 numeric(25,0) 范围内的非负整数。");
        }

        Value = value;
    }

    public decimal Value { get; }
}
