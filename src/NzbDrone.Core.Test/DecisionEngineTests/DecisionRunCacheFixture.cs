using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Test.Common;

namespace NzbDrone.Core.Test.DecisionEngineTests
{
    [TestFixture]
    public class DecisionRunCacheFixture : TestBase
    {
        [Test]
        public async Task should_share_values_across_threads_of_one_run_but_not_between_runs()
        {
            var loads = 0;

            async Task<int> RunAsync()
            {
                using (DecisionRunCache.Begin())
                {
                    var first = DecisionRunCache.Get("key", () => Interlocked.Increment(ref loads));

                    await Task.Run(() => DecisionRunCache.Get("key", () => Interlocked.Increment(ref loads)).Should().Be(first));

                    return first;
                }
            }

            var results = await Task.WhenAll(Task.Run(RunAsync), Task.Run(RunAsync));

            results.Should().OnlyHaveUniqueItems();
            loads.Should().Be(2);
            DecisionRunCache.Get("key", () => 0).Should().Be(0);
        }
    }
}
