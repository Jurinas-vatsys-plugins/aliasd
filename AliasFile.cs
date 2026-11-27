using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AliasPlugin
{
    /// <summary>
    /// Reads CRC / VRC style alias files:
    ///   .name expanded text $1 $arr ...
    /// Lines starting with ';' are comments; lines not starting with '.' (e.g. ##### or section titles) are ignored.
    /// </summary>
    internal static class AliasFile
    {
        public static int LoadInto(string path, Dictionary<string, string> target)
        {
            int count = 0;
            foreach (var raw in File.ReadLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length < 2 || line[0] != '.')
                    continue;

                int sp = IndexOfWhitespace(line);
                if (sp < 0)
                    continue; // name only, no text

                var name = line.Substring(0, sp).ToLowerInvariant();
                var text = line.Substring(sp).Trim();
                if (text.Length == 0)
                    continue;

                target[name] = text; // later definitions override earlier ones
                count++;
            }
            return count;
        }

        internal static int IndexOfWhitespace(string s, int start = 0)
        {
            for (int i = start; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i]))
                    return i;
            return -1;
        }
    }
}
