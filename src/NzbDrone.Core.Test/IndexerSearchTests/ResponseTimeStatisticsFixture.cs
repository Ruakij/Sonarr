using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.IndexerSearch;

namespace NzbDrone.Core.Test.IndexerSearchTests
{
    [TestFixture]
    public class ResponseTimeStatisticsFixture
    {
        [TestCase(new[] { 300.0, 100.0, 200.0 }, 200)]
        [TestCase(new[] { 400.0, 100.0, 200.0, 300.0 }, 250)]
        [TestCase(new[] { 700.0 }, 700)]
        public void should_return_median(double[] values, double median)
        {
            ResponseTimeStatistics.Median(values).Should().Be(median);
        }

        [TestCase(2.5, 2.5)]
        [TestCase(97.5, 97.5)]
        [TestCase(0, 0)]
        [TestCase(100, 100)]
        public void should_interpolate_percentile(double percentile, double expected)
        {
            ResponseTimeStatistics.Percentile(new[] { 100.0, 0.0 }, percentile).Should().BeApproximately(expected, 0.0001);
        }
    }
}
