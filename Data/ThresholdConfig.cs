/* In the name of God, the Merciful, the Compassionate */

using System.Collections.Generic;
namespace SQLTriage.Data
{
    public class Threshold
    {
        public double Value { get; set; }
        public string Color { get; set; } = "";
    }

    public static class ThresholdConfig
    {
        // Colors matching Grafana palette
        public const string DarkGreen = "#1b5e20";
        public const string Green = "#4caf50";
        public const string LightOrange = "#ff9800";
        public const string Orange = "#f57c00";
        public const string Red = "#f44336";
        public const string DarkPurple = "#7b1fa2";
        public const string Blue = "#2196f3";
        public const string Gray = "#616161";

        private static Dictionary<string, Threshold[]> _thresholds = new()
        {
            ["CPU Usage %"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 80, Color = LightOrange }, new Threshold { Value = 95, Color = Red } },
            ["Latency"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 20, Color = LightOrange }, new Threshold { Value = 50, Color = Red } },
            ["Wait Time"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 800, Color = LightOrange }, new Threshold { Value = 1000, Color = Red } },
            ["Bytes Transferred"] = new[] { new Threshold { Value = 0, Color = Blue } },
            ["Batch Requests"] = new[] { new Threshold { Value = 0, Color = Blue } },
            ["Page reads/sec"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 60, Color = LightOrange }, new Threshold { Value = 90, Color = Red } },
            ["Long Processes"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 1, Color = LightOrange } },
            ["Processes blocked"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 1, Color = Red } },
            ["SQL Memory"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 85, Color = LightOrange }, new Threshold { Value = 95, Color = Red } },
            ["Pending"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 1, Color = Red } },
            ["Transactions/sec"] = new[] { new Threshold { Value = 0, Color = Blue } },
            ["Waiting Tasks"] = new[] { new Threshold { Value = 0, Color = Blue } },
            ["Logins/sec"] = new[] { new Threshold { Value = 0, Color = Green }, new Threshold { Value = 2, Color = LightOrange }, new Threshold { Value = 4, Color = Red } },
            ["Checks"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 1, Color = LightOrange }, new Threshold { Value = 2, Color = Red }, new Threshold { Value = 3, Color = DarkPurple } },
            ["Blocked"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 1, Color = LightOrange }, new Threshold { Value = 2, Color = Red } },
            ["Memory"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 85, Color = LightOrange }, new Threshold { Value = 95, Color = Red } },
            ["Disk Space Used %"] = new[] { new Threshold { Value = 0, Color = DarkGreen }, new Threshold { Value = 80, Color = LightOrange }, new Threshold { Value = 90, Color = Red } },
        };

        public static string GetColor(string metric, double value)
        {
            if (string.IsNullOrEmpty(metric) || !_thresholds.TryGetValue(metric, out var thresholds))
                return Green;

            string color = Green;
            foreach (var t in thresholds)
            {
                if (value >= t.Value)
                    color = t.Color;
            }
            return color;
        }

        public static Threshold[] GetThresholds(string metric)
        {
            if (string.IsNullOrEmpty(metric)) return new[] { new Threshold { Value = 0, Color = Green } };
            return _thresholds.TryGetValue(metric, out var t) ? t : new[] { new Threshold { Value = 0, Color = Green } };
        }

        /// <summary>
        /// Updates the global thresholds. Useful for loading enterprise customizations from config.
        /// </summary>
        public static void UpdateThresholds(Dictionary<string, Threshold[]> newThresholds)
        {
            if (newThresholds != null)
            {
                _thresholds = newThresholds;
            }
        }
    }
}
