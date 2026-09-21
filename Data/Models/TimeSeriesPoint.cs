/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models
{
    public class TimeSeriesPoint
    {
        public DateTime Time { get; set; }
        public string Series { get; set; } = "";
        public double Value { get; set; }
    }
}
