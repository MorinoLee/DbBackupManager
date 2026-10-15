using DbBackupManager.Domain.BackupSets;

namespace DbBackupManager.Application.BackupPlans;

public sealed record BackupPlanBaselineObservation(
    bool HasVerifiedManagedOrdinaryFull,
    DifferentialBaselineConclusion? ActiveBaselineConclusion,
    bool HasOutstandingOrdinaryPlanFull)
{
    public string? Notice => ActiveBaselineConclusion switch
    {
        DifferentialBaselineConclusion.Unknown => BaselineUnknown,
        DifferentialBaselineConclusion.Unmanaged => BaselineUnmanaged,
        DifferentialBaselineConclusion.Mismatch => BaselineMismatch,
        _ => null,
    };

    public const string BaselineUnknown = "baseline_unknown";

    public const string BaselineUnmanaged = "baseline_unmanaged";

    public const string BaselineMismatch = "baseline_mismatch";
}
