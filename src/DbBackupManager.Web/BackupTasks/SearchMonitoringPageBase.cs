using System.Globalization;
using DbBackupManager.Application.BackupTasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace DbBackupManager.Web.BackupTasks;

public abstract class SearchMonitoringPageBase : MonitoringPageBase
{
    [Inject] protected NavigationManager Navigation { get; set; } = default!;
    [SupplyParameterFromQuery(Name = "page")] public int Page { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? Search { get; set; }
    [SupplyParameterFromQuery(Name = "status")] public string? Status { get; set; }
    [SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [SupplyParameterFromQuery(Name = "until")] public string? Until { get; set; }
    [SupplyParameterFromQuery(Name = "oldest")] public bool Oldest { get; set; }
    protected string? SearchInput { get; set; }
    protected string? StatusInput { get; set; }
    protected string? FromInput { get; set; }
    protected string? UntilInput { get; set; }
    protected bool OldestInput { get; set; }
    protected abstract string ListRoute { get; }
    protected string ListUrl => Url(Page, Search, Status, From, Until, Oldest);

    protected override Task OnParametersSetAsync()
    {
        SearchInput = Search; StatusInput = Status; FromInput = From; UntilInput = Until; OldestInput = Oldest;
        return base.OnParametersSetAsync();
    }

    protected BackupSearch Query() => new(Page, Search, Status, ParseDate(From, false), ParseDate(Until, true), Oldest);
    private static DateTimeOffset? ParseDate(string? text, bool end)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || end && date.Date == DateTime.MaxValue.Date)
            return Require(new BackupManagementResult<DateTimeOffset?>(BackupManagementCode.Invalid));
        return new DateTimeOffset(DateTime.SpecifyKind(end ? date.AddDays(1) : date, DateTimeKind.Local)).ToUniversalTime();
    }
    protected void ApplySearch() => Navigation.NavigateTo(Url(0, SearchInput, StatusInput, FromInput, UntilInput, OldestInput));
    protected void ResetSearch()
    {
        SearchInput = StatusInput = FromInput = UntilInput = null;
        OldestInput = false;
        Navigation.NavigateTo(ListRoute);
    }
    protected void Previous() => Navigation.NavigateTo(Url(Math.Max(0, Page - 1), Search, Status, From, Until, Oldest));
    protected void Next() => Navigation.NavigateTo(Url(Page + 1, Search, Status, From, Until, Oldest));
    private string Url(int page, string? search, string? status, string? from, string? until, bool oldest)
        => QueryHelpers.AddQueryString(ListRoute, new Dictionary<string, string?>
        {
            ["page"] = page == 0 ? null : page.ToString(CultureInfo.InvariantCulture),
            ["q"] = string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
            ["status"] = string.IsNullOrEmpty(status) ? null : status,
            ["from"] = string.IsNullOrEmpty(from) ? null : from,
            ["until"] = string.IsNullOrEmpty(until) ? null : until,
            ["oldest"] = oldest ? "true" : null,
        });
}
