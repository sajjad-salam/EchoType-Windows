using System.Globalization;
using EchoType.Ui;

namespace EchoType.Tasks;

/// <summary>Occurrence arithmetic for <see cref="Recurrence"/>, all in task-zone wall time.</summary>
internal static class RecurrenceMath {

    private const int MaxSteps = 3000;

    /// <summary>
    /// The first occurrence strictly after <paramref name="afterLocal"/>, counting from the
    /// series anchor <paramref name="anchorLocal"/> (the current due). Null when the series ended.
    /// </summary>
    public static DateTime? Next(Recurrence rule, DateTime anchorLocal, DateTime afterLocal) {
        int interval = Math.Max(1, rule.Interval);
        DateTime? until = rule.Until is { } u ? TaskTime.ToLocal(u).Date.AddDays(1) : null;
        DateTime? candidate = null;
        switch (rule.Frequency) {
            case RecurrenceFrequency.Daily: {
                double days = Math.Max(0, (afterLocal - anchorLocal).TotalDays);
                long k = (long)Math.Floor(days / interval);
                for (int i = 0; i < 4; i++, k++) {
                    var c = anchorLocal.AddDays(k * interval);
                    if (c > afterLocal) {
                        candidate = c;
                        break;
                    }
                }
                break;
            }
            case RecurrenceFrequency.Weekly: {
                var days = WeekDays(rule, anchorLocal);
                DateTime anchorWeek = WeekStart(anchorLocal.Date);
                // Jump close to "after" first so a long-missed series doesn't walk years of days.
                DateTime start = anchorLocal.Date;
                if (afterLocal.Date > start.AddDays(14)) {
                    start = WeekStart(afterLocal.Date).AddDays(-7);
                }
                for (int i = 0; i < MaxSteps; i++) {
                    var day = start.AddDays(i);
                    var c = day + anchorLocal.TimeOfDay;
                    if (c <= afterLocal || c <= anchorLocal || !days.Contains(day.DayOfWeek)) {
                        continue;
                    }
                    long weeks = (long)Math.Round((WeekStart(day) - anchorWeek).TotalDays / 7);
                    if (weeks % interval != 0) {
                        continue;
                    }
                    candidate = c;
                    break;
                }
                break;
            }
            case RecurrenceFrequency.Monthly:
            case RecurrenceFrequency.Yearly: {
                int months = rule.Frequency == RecurrenceFrequency.Monthly ? interval : interval * 12;
                for (int k = 1; k < MaxSteps; k++) {
                    var c = anchorLocal.AddMonths(k * months);
                    if (c > afterLocal) {
                        candidate = c;
                        break;
                    }
                }
                break;
            }
        }
        if (candidate is { } found && until is { } end && found >= end) {
            return null;
        }
        return candidate;
    }

    /// <summary>
    /// First occurrence on or after <paramref name="startLocal"/> for a new series: the start
    /// itself when it matches (e.g. a weekday in the list), otherwise the next match.
    /// </summary>
    public static DateTime FirstOnOrAfter(Recurrence rule, DateTime startLocal) {
        if (rule.Frequency != RecurrenceFrequency.Weekly || rule.Days.Count == 0
            || rule.Days.Contains(startLocal.DayOfWeek)) {
            return startLocal;
        }
        for (int i = 1; i <= 7; i++) {
            var c = startLocal.AddDays(i);
            if (rule.Days.Contains(c.DayOfWeek)) {
                return c;
            }
        }
        return startLocal;
    }

    private static HashSet<DayOfWeek> WeekDays(Recurrence rule, DateTime anchor) =>
        rule.Days.Count > 0 ? rule.Days.ToHashSet() : [anchor.DayOfWeek];

    /// <summary>Weeks start on Saturday, as in Iraq.</summary>
    private static DateTime WeekStart(DateTime date) {
        int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7;
        return date.AddDays(-diff);
    }

    public static string Describe(Recurrence rule) {
        int n = Math.Max(1, rule.Interval);
        string text = rule.Frequency switch {
            RecurrenceFrequency.Daily => n == 1 ? Loc.T("Every day") : Loc.F("Every {0} days", n),
            RecurrenceFrequency.Weekly when rule.Days.Count > 0 =>
                (n == 1 ? Loc.T("Every week") : Loc.F("Every {0} weeks", n)) + ": " + DayList(rule.Days),
            RecurrenceFrequency.Weekly => n == 1 ? Loc.T("Every week") : Loc.F("Every {0} weeks", n),
            RecurrenceFrequency.Monthly => n == 1 ? Loc.T("Every month") : Loc.F("Every {0} months", n),
            _ => n == 1 ? Loc.T("Every year") : Loc.F("Every {0} years", n),
        };
        if (rule.Until is { } until) {
            text += " " + Loc.F("until {0}", TaskTime.ToLocal(until).ToString("d MMM", CultureInfo.CurrentUICulture));
        }
        return text;
    }

    private static readonly DayOfWeek[] WeekOrder = [
        DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday,
        DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday,
    ];

    public static string DayList(IEnumerable<DayOfWeek> days) {
        var set = days.ToHashSet();
        var names = CultureInfo.CurrentUICulture.DateTimeFormat;
        return string.Join(Loc.Arabic ? "، " : ", ",
            WeekOrder.Where(set.Contains).Select(d => Loc.Arabic ? names.GetDayName(d) : names.GetAbbreviatedDayName(d)));
    }

    public static bool TryParseDay(string? text, out DayOfWeek day) {
        day = default;
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length < 2) {
            return false;
        }
        (string Key, DayOfWeek Day)[] map = [
            ("sun", DayOfWeek.Sunday), ("mon", DayOfWeek.Monday), ("tue", DayOfWeek.Tuesday),
            ("wed", DayOfWeek.Wednesday), ("thu", DayOfWeek.Thursday), ("fri", DayOfWeek.Friday),
            ("sat", DayOfWeek.Saturday), ("احد", DayOfWeek.Sunday), ("أحد", DayOfWeek.Sunday),
            ("اثنين", DayOfWeek.Monday), ("ثلاثاء", DayOfWeek.Tuesday), ("اربعاء", DayOfWeek.Wednesday),
            ("أربعاء", DayOfWeek.Wednesday), ("خميس", DayOfWeek.Thursday), ("جمعة", DayOfWeek.Friday),
            ("سبت", DayOfWeek.Saturday),
        ];
        foreach (var (key, d) in map) {
            if (t.StartsWith(key, StringComparison.Ordinal) || t.Contains(key, StringComparison.Ordinal)) {
                day = d;
                return true;
            }
        }
        if (int.TryParse(t, out int n) && n is >= 0 and <= 6) {
            day = (DayOfWeek)n;
            return true;
        }
        return false;
    }
}
