using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NzbDrone.Core.DecisionEngine
{
    // Specifications and lookups evaluate every release of a decision run against the same movie data,
    // which does not change while the run lasts, so they share one load per run instead of one per release.
    // Outside a run every lookup goes to its source.
    public static class DecisionRunCache
    {
        private static readonly AsyncLocal<ConcurrentDictionary<string, object>> Current = new ();

        public static IDisposable Begin()
        {
            if (Current.Value != null)
            {
                return null;
            }

            Current.Value = new ConcurrentDictionary<string, object>();

            return new Scope();
        }

        public static T Get<T>(string key, Func<T> fetch)
        {
            var values = Current.Value;

            if (values == null)
            {
                return fetch();
            }

            return (T)values.GetOrAdd(key, _ => fetch());
        }

        private sealed class Scope : IDisposable
        {
            public void Dispose()
            {
                Current.Value = null;
            }
        }
    }
}
