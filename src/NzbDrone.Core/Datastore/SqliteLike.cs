using System.Linq;

namespace NzbDrone.Core.Datastore
{
    // Evaluates "value LIKE '%' || pattern || '%'" in memory with SQLite rules: '%' and '_' in the pattern are wildcards
    // (no escape character), '_' matches one character (code point), only ASCII letters compare case insensitively, NULL never matches.
    public static class SqliteLike
    {
        public static bool Contains(string value, string pattern)
        {
            if (value == null || pattern == null)
            {
                return false;
            }

            return Match(ToCodePoints(value), ToCodePoints("%" + pattern + "%"));
        }

        private static int[] ToCodePoints(string text)
        {
            return text.EnumerateRunes().Select(r => FoldAscii(r.Value)).ToArray();
        }

        private static int FoldAscii(int c)
        {
            return c is >= 'A' and <= 'Z' ? c + ('a' - 'A') : c;
        }

        private static bool Match(int[] value, int[] pattern)
        {
            int v = 0, p = 0, starP = -1, starV = 0;

            while (v < value.Length)
            {
                if (p < pattern.Length && pattern[p] == '%')
                {
                    starP = p++;
                    starV = v;
                }
                else if (p < pattern.Length && (pattern[p] == '_' || pattern[p] == value[v]))
                {
                    p++;
                    v++;
                }
                else if (starP >= 0)
                {
                    p = starP + 1;
                    v = ++starV;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '%')
            {
                p++;
            }

            return p == pattern.Length;
        }
    }
}
