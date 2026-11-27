using System;
using System.Linq;
using System.Text.RegularExpressions;
using vatsys;

namespace AliasPlugin
{
    /// <summary>
    /// Replaces $1..$9 and variables such as $squawk / $arr / $freq(XX) in alias text with live vatSys data.
    /// Unknown variables, or ones with no data available, are left as-is so they can be filled in by hand.
    /// </summary>
    internal class Variables
    {
        private static readonly Regex PositionalRx = new Regex(@"\$(\d+)", RegexOptions.Compiled);
        // Plain variables: $arr, $squawk (not followed by '(' or a letter)
        private static readonly Regex PlainVarRx = new Regex(@"\$([A-Za-z]+)(?![A-Za-z(])", RegexOptions.Compiled);
        // Functions with an argument: $freq(CTR), $uc($1)
        private static readonly Regex FuncVarRx = new Regex(@"\$([A-Za-z]+)\(([^()]*)\)", RegexOptions.Compiled);

        /// <summary>Variables that could not be resolved and must be filled in by the user ($1, $metar(KLAX), $wind($1) ...).</summary>
        public static readonly Regex PlaceholderRx = new Regex(@"\$[A-Za-z0-9]+(?:\([^()]*\))?", RegexOptions.Compiled);

        private readonly Config cfg;

        public Variables(Config cfg) { this.cfg = cfg; }

        public string Expand(string text, string[] args, string targetCallsign)
        {
            // 1) positional arguments $1 $2 ...
            text = PositionalRx.Replace(text, m =>
            {
                int n = int.Parse(m.Groups[1].Value);
                return n >= 1 && n <= args.Length ? args[n - 1] : m.Value;
            });

            // 2) named variables / functions such as $arr, $freq(CTR)
            FDP2.FDR fdr = null;
            bool fdrLooked = false;
            FDP2.FDR Fdr()
            {
                if (!fdrLooked)
                {
                    fdrLooked = true;
                    fdr = FindFdr(targetCallsign);
                }
                return fdr;
            }

            // Plain variables first (so the $arr in $wind($arr) becomes an airport code first).
            text = PlainVarRx.Replace(text, m => Safe(() => Resolve(m.Groups[1].Value.ToLowerInvariant(), false, "", Fdr, targetCallsign)) ?? m.Value);

            // Then functions; keep them as-is if the argument still contains an unfilled $ variable.
            text = FuncVarRx.Replace(text, m =>
            {
                string arg = m.Groups[2].Value.Trim();
                if (arg.Contains("$")) return m.Value;
                return Safe(() => Resolve(m.Groups[1].Value.ToLowerInvariant(), true, arg, Fdr, targetCallsign)) ?? m.Value;
            });
            return text;
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private string Resolve(string name, bool hasParens, string arg, Func<FDP2.FDR> fdr, string target)
        {
            switch (name)
            {
                // ---------- target aircraft ----------
                case "aircraft":
                case "acid":
                    return Empty(target ?? fdr()?.Callsign);
                case "squawk":
                    {
                        var f = fdr();
                        if (f == null || f.AssignedSSRCode < 0) return null;
                        return Convert.ToString(f.AssignedSSRCode, 8).PadLeft(4, '0');
                    }
                case "arr":
                case "dest":
                    return Empty(fdr()?.DesAirport);
                case "dep":
                    return Empty(fdr()?.DepAirport);
                case "route":
                    return Empty(fdr()?.Route);
                case "type":
                case "actype":
                    return Empty(fdr()?.AircraftType);
                case "cruise":
                    {
                        var f = fdr();
                        return f == null ? null : FormatAlt(f.RFL);
                    }
                case "temp":
                    return TempAlt(fdr());
                case "alt":
                    {
                        var f = fdr();
                        if (f == null) return null;
                        return TempAlt(f) ?? FormatAlt(f.RFL);
                    }
                case "calt":
                    {
                        var p = FindPilot(target ?? fdr()?.Callsign);
                        if (p == null) return null;
                        return FormatAlt((int)(Math.Round(p.PressureAltitude / 100.0) * 100));
                    }

                // ---------- yourself ----------
                case "callsign":
                case "mycallsign":
                    return Empty(Network.Callsign);
                case "myrealname":
                    return Empty(Network.RealName);
                case "com1":
                    return MyFrequency();
                case "freq":
                    return hasParens && arg != "" ? StationFrequency(arg) : MyFrequency();
                case "radioname":
                    return hasParens && arg != "" ? StationName(arg) : MyName();
                case "time":
                case "utc":
                    return DateTime.UtcNow.ToString("HHmm");
                case "uc":
                    return hasParens && arg != "" ? arg.ToUpperInvariant() : null;
                case "lc":
                    return hasParens && arg != "" ? arg.ToLowerInvariant() : null;
            }
            return null; // unsupported (e.g. $metar, $winds): leave as-is
        }

        // ---------------- helpers ----------------

        private static string Empty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private string FormatAlt(int feet)
        {
            if (feet <= 2) return null; // -1 = not set, 1/2 = vatSys special values
            if (feet >= cfg.TransitionAltitude)
                return "FL" + (feet / 100).ToString("000");
            return feet.ToString();
        }

        private string TempAlt(FDP2.FDR f)
        {
            if (f == null) return null;
            if (f.CFLUpper > 2) return FormatAlt(f.CFLUpper);
            if (f.CFLLower > 2) return FormatAlt(f.CFLLower);
            return null;
        }

        private static FDP2.FDR FindFdr(string callsign)
        {
            if (!string.IsNullOrEmpty(callsign))
            {
                var f = FDP2.GetFDRs?.FirstOrDefault(x => string.Equals(x.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
                if (f != null) return f;
            }
            return MMI.SelectedTrack?.GetFDR();
        }

        private static NetworkPilot FindPilot(string callsign)
        {
            if (string.IsNullOrEmpty(callsign)) return null;
            return Network.GetOnlinePilots?.FirstOrDefault(p => string.Equals(p.Callsign, callsign, StringComparison.OrdinalIgnoreCase));
        }

        private static string MyFrequency()
        {
            var freqs = Network.TransmittingFrequencies;
            if (freqs != null && freqs.Length > 0 && freqs[0] > 0 && freqs[0] != 99998)
                return Conversions.FSDFrequencyToString(freqs[0]);

            var sector = MMI.SectorsControlled?.FirstOrDefault(s => !s.IsDummy);
            if (sector != null && sector.Frequency > 0)
                return Conversions.FrequencyToString(sector.Frequency);
            return null;
        }

        private static string MyName()
        {
            var sector = MMI.SectorsControlled?.FirstOrDefault(s => !s.IsDummy);
            if (sector != null && !string.IsNullOrWhiteSpace(sector.FullName)) return sector.FullName;
            return Empty(Network.Callsign);
        }

        private static SectorsVolumes.Sector FindSector(string id)
        {
            return SectorsVolumes.Sectors?.FirstOrDefault(s =>
                       string.Equals(s.Name, id, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(s.Callsign, id, StringComparison.OrdinalIgnoreCase))
                   ?? SectorsVolumes.Sectors?.FirstOrDefault(s =>
                       !string.IsNullOrEmpty(s.Callsign) && s.Callsign.StartsWith(id, StringComparison.OrdinalIgnoreCase));
        }

        private static NetworkATC FindAtc(string id)
        {
            var list = Network.GetOnlineATCs;
            if (list == null) return null;
            return list.FirstOrDefault(a => string.Equals(a.Callsign, id, StringComparison.OrdinalIgnoreCase))
                   ?? list.FirstOrDefault(a => a.Callsign != null && a.Callsign.StartsWith(id, StringComparison.OrdinalIgnoreCase));
        }

        private static string StationFrequency(string id)
        {
            var atc = FindAtc(id);
            if (atc?.Frequencies != null && atc.Frequencies.Length > 0 && atc.Frequencies[0] > 0 && atc.Frequencies[0] != 99998)
                return Conversions.FSDFrequencyToString(atc.Frequencies[0]);
            var sector = FindSector(id);
            if (sector != null && sector.Frequency > 0)
                return Conversions.FrequencyToString(sector.Frequency);
            return null;
        }

        private static string StationName(string id)
        {
            var sector = FindSector(id);
            if (sector != null && !string.IsNullOrWhiteSpace(sector.FullName) && !sector.IsDummy) return sector.FullName;
            var atc = FindAtc(id);
            return Empty(atc?.Callsign);
        }
    }
}
