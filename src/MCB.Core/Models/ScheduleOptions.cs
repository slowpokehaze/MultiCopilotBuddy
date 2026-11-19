using System.Text.Json.Serialization;

namespace MCB.Core.Models;

/// <summary>
/// Расписание бота по аккаунту (Этап 6): ботят только внутри окна.
/// Вне окна SessionManager останавливает клиента и не перезапускает.
/// </summary>
public sealed class ScheduleOptions
{
    public bool Enabled { get; set; }

    /// <summary>Начало окна "HH:mm" (локальное время).</summary>
    public string Start { get; set; } = "00:00";

    /// <summary>Конец окна "HH:mm" (локальное время). Окно может переходить через полночь.</summary>
    public string End { get; set; } = "00:00";

    /// <summary>Дни недели: "Mo,Tu,We,Th,Fr,Sa,Su" или "all".</summary>
    public string Days { get; set; } = "all";

    [JsonIgnore]
    public bool IsActiveNow => IsWindowActive(this, DateTime.Now);

    /// <summary>Активно ли окно в заданный момент (тестируемо).</summary>
    public static bool IsWindowActive(ScheduleOptions schedule, DateTime now)
    {
        if (!schedule.Enabled)
        {
            return true; // расписание выключено — ботим всегда
        }

        if (!MatchesDay(schedule, now.DayOfWeek))
        {
            return false;
        }

        if (!TimeSpan.TryParse(schedule.Start, out var start) || !TimeSpan.TryParse(schedule.End, out var end))
        {
            return true; // кривое расписание не должно стопорить бота
        }

        var time = now.TimeOfDay;
        if (start == end)
        {
            return true; // 24ч
        }

        return start < end
            ? time >= start && time < end
            : time >= start || time < end; // через полночь
    }

    private static bool MatchesDay(ScheduleOptions schedule, DayOfWeek day)
    {
        var days = schedule.Days;
        if (string.IsNullOrWhiteSpace(days) || days.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var map = new Dictionary<string, DayOfWeek>(StringComparer.OrdinalIgnoreCase)
        {
            ["mo"] = DayOfWeek.Monday, ["tu"] = DayOfWeek.Tuesday, ["we"] = DayOfWeek.Wednesday,
            ["th"] = DayOfWeek.Thursday, ["fr"] = DayOfWeek.Friday, ["sa"] = DayOfWeek.Saturday,
            ["su"] = DayOfWeek.Sunday,
        };
        return days.Split(',')
            .Select(d => d.Trim())
            .Where(d => d.Length > 0)
            .Any(d => map.TryGetValue(d, out var mapped) && mapped == day);
    }
}
