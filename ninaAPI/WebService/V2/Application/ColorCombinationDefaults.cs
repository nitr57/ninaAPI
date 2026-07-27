#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;
using System.Linq;

namespace ninaAPI.WebService.V2
{
    /// <summary>
    /// Guesses which filter belongs on which colour channel. Mirrors the heuristic of the
    /// livestack plugin's own "Color Combination Wizard" so the API suggests the same mapping
    /// the desktop UI would. Levenshtein is implemented here instead of pulling in a package,
    /// to keep the plugin's shipped dependencies unchanged.
    /// </summary>
    internal static class ColorCombinationDefaults
    {
        /// <summary>
        /// When the filter names look more like a broadband set ("Red") than a narrowband set
        /// ("HA"), an RGB mapping is suggested, otherwise SHO - or HA/OIII/OIII when there are
        /// fewer than three filters to pick from.
        /// </summary>
        public static (string Red, string Green, string Blue) Suggest(IReadOnlyList<string> filters)
        {
            if (filters is null || filters.Count == 0)
                return (null, null, null);

            int distanceRed = int.MaxValue;
            int distanceHa = int.MaxValue;
            foreach (string filter in filters)
            {
                string f = filter?.ToLowerInvariant() ?? string.Empty;
                distanceRed = Math.Min(distanceRed, Distance("red", f));
                distanceHa = Math.Min(distanceHa, Distance("ha", f));
            }

            if (distanceRed <= distanceHa)
                return (BestMatch(filters, "Red"), BestMatch(filters, "Green"), BestMatch(filters, "Blue"));

            if (filters.Count < 3)
                return (BestMatch(filters, "HA"), BestMatch(filters, "OIII"), BestMatch(filters, "OIII"));

            return (BestMatch(filters, "SII"), BestMatch(filters, "HA"), BestMatch(filters, "OIII"));
        }

        public static string BestMatch(IReadOnlyList<string> filters, string channel)
        {
            if (filters is null || filters.Count == 0)
                return null;

            string target = channel.ToLowerInvariant();
            return filters.OrderBy(x => Distance(target, x?.ToLowerInvariant() ?? string.Empty)).First();
        }

        /// <summary>Levenshtein edit distance, two-row variant.</summary>
        private static int Distance(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            int[] previous = new int[b.Length + 1];
            int[] current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
