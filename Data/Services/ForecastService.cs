/* In the name of God, the Merciful, the Compassionate */

using SQLTriage.Data.Caching;
using SQLTriage.Data.Models;
using Microsoft.Extensions.Logging;

namespace SQLTriage.Data.Services
{
    /// <summary>
    /// Provides linear regression-based forecasting for time-series metrics.
    /// Uses cached historical data to predict when thresholds will be breached.
    /// </summary>
    public class ForecastService
    {
        private readonly liveQueriesCacheStore _cache;
        private readonly ILogger<ForecastService> _logger;

        public ForecastService(liveQueriesCacheStore cache, ILogger<ForecastService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        /// <summary>
        /// Result of a forecast calculation.
        /// </summary>
        public record ForecastResult
        {
            /// <summary>Current value (latest data point).</summary>
            public double CurrentValue { get; init; }
            /// <summary>Slope per day (rate of change).</summary>
            public double SlopePerDay { get; init; }
            /// <summary>Predicted days until the threshold is reached. Null if trend is flat or moving away.</summary>
            public double? DaysUntilThreshold { get; init; }
            /// <summary>Predicted date when threshold is reached. Null if not applicable.</summary>
            public DateTime? PredictedDate { get; init; }
            /// <summary>R-squared value (0-1) indicating fit quality. Above 0.5 is reasonable.</summary>
            public double RSquared { get; init; }
            /// <summary>Number of data points used for the regression.</summary>
            public int DataPointCount { get; init; }
            /// <summary>Whether the forecast is reliable (enough data + reasonable R²).</summary>
            public bool IsReliable => DataPointCount >= 10 && RSquared >= 0.3;
        }

        /// <summary>
        /// Calculates a linear forecast for when a metric will reach a threshold.
        /// </summary>
        /// <param name="queryId">The panel/query ID in the cache.</param>
        /// <param name="instanceKey">The instance key in the cache.</param>
        /// <param name="seriesName">Which series within the query to forecast (e.g., "Processor Time %").</param>
        /// <param name="threshold">The threshold value to predict against.</param>
        /// <param name="lookbackHours">How many hours of historical data to use. Default 168 (7 days).</param>
        public async Task<ForecastResult?> ForecastAsync(
            string queryId, string instanceKey, string seriesName,
            double threshold, int lookbackHours = 168)
        {
            try
            {
                var from = DateTime.UtcNow.AddHours(-lookbackHours);
                var to = DateTime.UtcNow;
                var data = await _cache.GetTimeSeriesAsync(queryId, instanceKey, from, to);

                var filtered = data
                    .Where(p => string.Equals(p.Series, seriesName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(p => p.Time)
                    .ToList();

                if (filtered.Count < 5)
                    return null;

                return CalculateLinearForecast(filtered, threshold);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Forecast calculation failed for {QueryId}/{Series}", queryId, seriesName);
                return null;
            }
        }

        /// <summary>
        /// Runs linear regression on the data points and predicts threshold breach.
        /// </summary>
        public static ForecastResult CalculateLinearForecast(List<TimeSeriesPoint> data, double threshold)
        {
            var baseTime = data[0].Time;
            int n = data.Count;

            // X = hours since first point, Y = value
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0, sumY2 = 0;
            for (int i = 0; i < n; i++)
            {
                double x = (data[i].Time - baseTime).TotalHours;
                double y = data[i].Value;
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
                sumY2 += y * y;
            }

            double meanX = sumX / n;
            double meanY = sumY / n;
            double denom = sumX2 - n * meanX * meanX;

            if (Math.Abs(denom) < 1e-10)
                return new ForecastResult
                {
                    CurrentValue = data[^1].Value,
                    SlopePerDay = 0,
                    DataPointCount = n,
                    RSquared = 0
                };

            double slope = (sumXY - n * meanX * meanY) / denom;  // per hour
            double intercept = meanY - slope * meanX;

            // R-squared
            double ssRes = 0, ssTot = 0;
            for (int i = 0; i < n; i++)
            {
                double x = (data[i].Time - baseTime).TotalHours;
                double predicted = slope * x + intercept;
                ssRes += (data[i].Value - predicted) * (data[i].Value - predicted);
                ssTot += (data[i].Value - meanY) * (data[i].Value - meanY);
            }
            double rSquared = ssTot > 0 ? 1 - ssRes / ssTot : 0;

            double slopePerDay = slope * 24;
            double currentValue = data[^1].Value;
            double currentX = (data[^1].Time - baseTime).TotalHours;

            // Predict when threshold is reached
            double? daysUntil = null;
            DateTime? predictedDate = null;

            if (Math.Abs(slope) > 1e-10)
            {
                double hoursToThreshold = (threshold - (slope * currentX + intercept)) / slope;
                if (hoursToThreshold > 0) // Only if threshold is in the future
                {
                    daysUntil = hoursToThreshold / 24;
                    predictedDate = data[^1].Time.AddHours(hoursToThreshold);
                }
            }

            return new ForecastResult
            {
                CurrentValue = currentValue,
                SlopePerDay = slopePerDay,
                DaysUntilThreshold = daysUntil,
                PredictedDate = predictedDate,
                RSquared = Math.Max(0, rSquared),
                DataPointCount = n
            };
        }
    }
}
