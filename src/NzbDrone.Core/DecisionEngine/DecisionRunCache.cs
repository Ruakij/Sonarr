using System;
using System.Collections.Generic;

namespace NzbDrone.Core.DecisionEngine
{
    // Shares data the specifications load per release across all releases of one decision run, so it is
    // loaded once per run. Nothing a decision depends on is written during a run, so the values cannot go
    // stale. It is ambient per thread because the specifications only receive the release; outside a run
    // every lookup loads directly.
    public sealed class DecisionRunCache : IDisposable
    {
        [ThreadStatic]
        private static DecisionRunCache _current;

        private readonly Dictionary<(string Kind, object Key), object> _values = new ();

        private DecisionRunCache()
        {
        }

        public static bool Active => _current != null;

        // Returns null for a nested run, which keeps sharing the outer run.
        public static IDisposable Begin()
        {
            if (_current != null)
            {
                return null;
            }

            _current = new DecisionRunCache();

            return _current;
        }

        public static T GetOrAdd<T>(string kind, object key, Func<T> load)
        {
            var run = _current;

            if (run == null)
            {
                return load();
            }

            if (run._values.TryGetValue((kind, key), out var value))
            {
                return (T)value;
            }

            var loaded = load();
            run._values[(kind, key)] = loaded;

            return loaded;
        }

        public void Dispose()
        {
            if (_current == this)
            {
                _current = null;
            }
        }
    }
}
