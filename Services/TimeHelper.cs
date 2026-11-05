using System.Globalization;

namespace MenuScan.Services
{
    public interface ITimeHelper
    {
        TimeZoneInfo TimeZone { get; }
        DateTime Now();
        DateTime Today();
        DateTime ToLocal(DateTime utc);
        DateTime ToUtc(DateTime local);
        (DateTime StartUtc, DateTime EndUtc) LocalDayRangeUtc(DateTime localDate);
        (DateTime StartUtc, DateTime EndUtc) LocalMonthRangeUtc(int year, int month);
        (int Year, int Month) SafeMonthYear(int? year, int? month);
        string Format(DateTime? dt, string format = "dd MMM yyyy, hh:mm tt");
    }

    public class TimeHelper : ITimeHelper
    {
        private readonly TimeZoneInfo _timeZone;

        public static ITimeHelper Current { get; set; } = new TimeHelper();

        public TimeZoneInfo TimeZone => _timeZone;

        public TimeHelper(IConfiguration? configuration = null)
        {
            var zoneName = configuration?["App:TimeZone"];
            _timeZone = ResolveTimeZone(zoneName);
        }

        public DateTime Now() => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone), DateTimeKind.Local);

        public DateTime Today() => DateTime.SpecifyKind(Now().Date, DateTimeKind.Local);

        public DateTime ToLocal(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local) return utc;
            var utcSpecified = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utcSpecified, _timeZone), DateTimeKind.Local);
        }

        public DateTime ToUtc(DateTime local)
        {
            if (local.Kind == DateTimeKind.Utc) return local;
            var localSpecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTimeToUtc(localSpecified, _timeZone);
        }

        public (DateTime StartUtc, DateTime EndUtc) LocalDayRangeUtc(DateTime localDate)
        {
            var startLocal = new DateTime(localDate.Year, localDate.Month, localDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddDays(1);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, _timeZone);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, _timeZone);
            return (startUtc, endUtc);
        }

        public (DateTime StartUtc, DateTime EndUtc) LocalMonthRangeUtc(int year, int month)
        {
            var (validYear, validMonth) = SafeMonthYear(year, month);
            var startLocal = new DateTime(validYear, validMonth, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var endLocal = startLocal.AddMonths(1);
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, _timeZone);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, _timeZone);
            return (startUtc, endUtc);
        }

        public (int Year, int Month) SafeMonthYear(int? year, int? month)
        {
            var today = Today();
            int y = year ?? today.Year;
            int m = month ?? today.Month;

            if (y < 2000 || y > 2100 || m < 1 || m > 12)
            {
                return (today.Year, today.Month);
            }

            return (y, m);
        }

        public string Format(DateTime? dt, string format = "dd MMM yyyy, hh:mm tt")
        {
            if (!dt.HasValue) return string.Empty;
            return ToLocal(dt.Value).ToString(format, CultureInfo.InvariantCulture);
        }

        private static TimeZoneInfo ResolveTimeZone(string? zoneName)
        {
            if (!string.IsNullOrWhiteSpace(zoneName))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(zoneName.Trim());
                }
                catch { }
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
            }
            catch { }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            }
            catch { }

            return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "India Standard Time", "India Standard Time");
        }
    }
}
