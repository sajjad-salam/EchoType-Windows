using System.Globalization;

namespace EchoType.Tasks;

/// <summary>
/// The task clock: every due date is read and shown in one time zone (Baghdad, UTC+3, no
/// daylight saving, unless changed in config.json) whatever zone Windows is set to.
/// </summary>
internal static class TaskTime {

    public const string DefaultZoneId = "Asia/Baghdad";

    private static TimeZoneInfo _zone = Resolve(DefaultZoneId);

    public static TimeZoneInfo Zone => _zone;

    /// <summary>Overridable in tests.</summary>
    public static Func<DateTimeOffset> UtcNowSource { get; set; } = () => DateTimeOffset.UtcNow;

    public static DateTimeOffset UtcNow => UtcNowSource();

    /// <summary>Wall-clock time in the task zone.</summary>
    public static DateTime LocalNow => ToLocal(UtcNow);

    public static void Configure(string? zoneId) {
        _zone = Resolve(string.IsNullOrWhiteSpace(zoneId) ? DefaultZoneId : zoneId.Trim());
    }

    /// <summary>
    /// IANA ids work on Windows 10 1903+ through ICU; the Windows id is the fallback, and a
    /// fixed UTC+3 zone keeps Baghdad time right even when neither is installed.
    /// </summary>
    private static TimeZoneInfo Resolve(string id) {
        foreach (string candidate in new[] { id, id == DefaultZoneId ? "Arabic Standard Time" : "" }) {
            if (candidate.Length == 0) {
                continue;
            }
            try {
                return TimeZoneInfo.FindSystemTimeZoneById(candidate);
            } catch (Exception) {
                // try the next spelling
            }
        }
        if (id != DefaultZoneId) {
            Log.Write("tasks: unknown time zone \"" + id + "\", using Baghdad");
        }
        return TimeZoneInfo.CreateCustomTimeZone("Baghdad", TimeSpan.FromHours(3), "Baghdad (UTC+3)", "Baghdad");
    }

    public static DateTime ToLocal(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, _zone).DateTime;

    /// <summary>A wall-clock time in the task zone → UTC.</summary>
    public static DateTimeOffset FromLocal(DateTime local) {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_zone.IsInvalidTime(local)) {
            local = local.AddHours(1);
        }
        var offset = _zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>"2026-10-30T17:00" (or a date alone) read as task-zone wall time.</summary>
    public static bool TryParseLocal(string? text, out DateTime local, out bool hasTime) {
        local = default;
        hasTime = false;
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }
        text = text.Trim();
        string[] dateOnly = ["yyyy-MM-dd", "yyyy/MM/dd"];
        if (DateTime.TryParseExact(text, dateOnly, CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) {
            return true;
        }
        string[] withTime = [
            "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd'T'HH:mm:ss.fff",
        ];
        if (DateTime.TryParseExact(text, withTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out local)) {
            hasTime = true;
            return true;
        }
        // An explicit offset ("…+03:00", "…Z") is converted into the task zone.
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) {
            local = ToLocal(dto);
            hasTime = true;
            return true;
        }
        return false;
    }

    public static string FormatIso(DateTimeOffset utc) =>
        ToLocal(utc).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Short human label: "Today 17:00", "Tomorrow", "Thu 31 Oct 09:00".</summary>
    public static string Describe(DateTimeOffset utc, bool hasTime, CultureInfo? culture = null) {
        culture ??= CultureInfo.CurrentUICulture;
        var local = ToLocal(utc);
        var today = LocalNow.Date;
        string day;
        if (local.Date == today) {
            day = Ui.Loc.T("Today");
        } else if (local.Date == today.AddDays(1)) {
            day = Ui.Loc.T("Tomorrow");
        } else if (local.Date == today.AddDays(-1)) {
            day = Ui.Loc.T("Yesterday");
        } else if (Math.Abs((local.Date - today).TotalDays) < 7) {
            day = local.ToString("dddd", culture);
        } else {
            day = local.ToString(local.Year == today.Year ? "ddd d MMM" : "ddd d MMM yyyy", culture);
        }
        return hasTime ? day + " " + local.ToString("HH:mm", CultureInfo.InvariantCulture) : day;
    }
}
