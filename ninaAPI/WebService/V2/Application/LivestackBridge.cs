#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;

namespace ninaAPI.WebService.V2
{
    /// <summary>
    /// Drives the livestack plugin's colour combination without requiring any change to that
    /// plugin. The livestack plugin is a fork that regularly takes upstream updates, so every
    /// local modification makes merging expensive - therefore the whole feature lives here.
    ///
    /// This is the single place in the API that reaches into another plugin's types. Everything
    /// is resolved reflectively and guarded, so an incompatible livestack version produces a
    /// clear error message instead of an exception somewhere else.
    ///
    /// Expected livestack shape:
    ///   NINA.Plugin.Livestack.LivestackMediator.LiveStackDockable        (public static)
    ///   LivestackDockable.Tabs                                           (IList of IStackTab)
    ///   IStackTab.Target / .Filter / .Locked / .StackImage
    ///   ColorCombinationTab(IProfileService, LiveStackTab red, LiveStackTab green,
    ///                       LiveStackTab blue, bool channelsAlreadyAligned)
    ///   ColorCombinationTab.Refresh(CancellationToken) -> Task
    ///   ColorCombinationTab.StackCountRed / .StackCountGreen / .StackCountBlue
    /// </summary>
    internal static class LivestackBridge
    {
        public const string RgbFilter = "RGB";

        // Filter names the plugin gives the three channels it extracts from a bayered frame
        // (LiveStackBag.RED_OSC / GREEN_OSC / BLUE_OSC). Those channels come out of the same
        // debayered image and are therefore already aligned to each other.
        private static readonly string[] OscFilters = { "R_OSC", "G_OSC", "B_OSC" };

        private const string MediatorTypeName = "NINA.Plugin.Livestack.LivestackMediator";
        private const string ColorCombinationTabTypeName = "NINA.Plugin.Livestack.LivestackDockables.ColorCombinationTab";
        private const string LiveStackTabTypeName = "NINA.Plugin.Livestack.LivestackDockables.LiveStackTab";
        private const string AssemblyName = "nina.plugin.livestack";

        private static readonly object resolveLock = new object();
        private static Assembly livestackAssembly;
        private static Type mediatorType;
        private static Type colorCombinationTabType;
        private static Type liveStackTabType;
        private static ConstructorInfo colorCombinationTabCtor;

        /// <summary>
        /// Remembers the livestack assembly from a broadcast we received from it. This is the
        /// most reliable handle - the content object is created by the livestack plugin itself.
        /// </summary>
        public static void LearnAssemblyFrom(object broadcastContent)
        {
            if (livestackAssembly is not null || broadcastContent is null)
                return;

            lock (resolveLock)
            {
                if (livestackAssembly is null)
                    livestackAssembly = broadcastContent.GetType().Assembly;
            }
        }

        public static bool IsAvailable => Resolve(out _);

        public static string UnavailableReason
        {
            get
            {
                Resolve(out string reason);
                return reason;
            }
        }

        private static bool Resolve(out string reason)
        {
            lock (resolveLock)
            {
                if (colorCombinationTabCtor is not null)
                {
                    reason = null;
                    return true;
                }

                try
                {
                    livestackAssembly ??= AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.OrdinalIgnoreCase));

                    if (livestackAssembly is null)
                    {
                        reason = "The livestack plugin is not loaded";
                        return false;
                    }

                    mediatorType = livestackAssembly.GetType(MediatorTypeName);
                    colorCombinationTabType = livestackAssembly.GetType(ColorCombinationTabTypeName);
                    liveStackTabType = livestackAssembly.GetType(LiveStackTabTypeName);

                    if (mediatorType is null || colorCombinationTabType is null || liveStackTabType is null)
                    {
                        reason = "The installed livestack version does not expose the expected types";
                        return false;
                    }

                    // The first parameter is IProfileService, then the three channel tabs, then
                    // the channelsAlreadyAligned flag (which has a default value upstream).
                    colorCombinationTabCtor = colorCombinationTabType.GetConstructors()
                        .FirstOrDefault(c =>
                        {
                            var p = c.GetParameters();
                            return p.Length == 5
                                && typeof(IProfileService).IsAssignableFrom(p[0].ParameterType)
                                && p[1].ParameterType == liveStackTabType
                                && p[2].ParameterType == liveStackTabType
                                && p[3].ParameterType == liveStackTabType
                                && p[4].ParameterType == typeof(bool);
                        });

                    if (colorCombinationTabCtor is null)
                    {
                        reason = "The installed livestack version has an incompatible ColorCombinationTab constructor "
                            + "(expected ColorCombinationTab(IProfileService, LiveStackTab, LiveStackTab, LiveStackTab, bool))";
                        return false;
                    }

                    reason = null;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                    reason = $"Could not access the livestack plugin: {ex.Message}";
                    return false;
                }
            }
        }

        private static IList GetTabs()
        {
            object dockable = mediatorType.GetProperty("LiveStackDockable", BindingFlags.Public | BindingFlags.Static)
                ?.GetValue(null)
                ?? throw new InvalidOperationException("The livestack dockable has not been created yet");

            return dockable.GetType().GetProperty("Tabs")?.GetValue(dockable) as IList
                ?? throw new InvalidOperationException("The livestack dockable does not expose its tabs");
        }

        /// <summary>
        /// Tabs is an AsyncObservableCollection without any thread safety, and the stacker adds
        /// to it from its worker. Never enumerate it directly - take a copy. The tab objects are
        /// the same instances, so state such as Locked is still read live off them.
        /// </summary>
        private static List<object> SnapshotTabs(IList tabs)
        {
            while (true)
            {
                try
                {
                    var snapshot = new List<object>(tabs.Count);
                    foreach (object tab in tabs)
                        snapshot.Add(tab);
                    return snapshot;
                }
                catch (InvalidOperationException)
                {
                    // "Collection was modified" - the stacker added a tab while we copied, retry
                }
            }
        }

        private static List<object> SnapshotTabs() => SnapshotTabs(GetTabs());

        /// <summary>
        /// True when the plugin saves stacked lights, in which case it refreshes and broadcasts
        /// the colour tab on every frame all by itself (ShouldRefreshColorTab). The API must not
        /// render a second time then - ColorCombinationTab.Refresh has no reentrancy guard.
        /// </summary>
        public static bool PluginRefreshesColorTabItself()
        {
            try
            {
                object plugin = mediatorType?.GetProperty("Plugin", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return plugin?.GetType().GetProperty("SaveStackedLights")?.GetValue(plugin) as bool? ?? false;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                return false;
            }
        }

        private static string ReadString(object tab, string property) => tab.GetType().GetProperty(property)?.GetValue(tab)?.ToString();

        private static bool IsColorCombination(object tab) => colorCombinationTabType.IsInstanceOfType(tab);

        public sealed class TabInfo
        {
            public string Target { get; init; }
            public string Filter { get; init; }
            public bool IsColorCombination { get; init; }
            public bool Locked { get; init; }
        }

        public static IReadOnlyList<TabInfo> ListTabs()
        {
            var result = new List<TabInfo>();
            foreach (object tab in SnapshotTabs())
            {
                if (tab is null)
                    continue;
                result.Add(new TabInfo()
                {
                    Target = ReadString(tab, "Target"),
                    Filter = ReadString(tab, "Filter"),
                    IsColorCombination = IsColorCombination(tab),
                    Locked = tab.GetType().GetProperty("Locked")?.GetValue(tab) as bool? ?? false,
                });
            }
            return result;
        }

        /// <summary>Result of creating or refreshing a colour combination.</summary>
        public sealed class ColorCombinationInfo
        {
            public string Target { get; init; }
            public string RedFilter { get; init; }
            public string GreenFilter { get; init; }
            public string BlueFilter { get; init; }
            public int RedStackCount { get; init; }
            public int GreenStackCount { get; init; }
            public int BlueStackCount { get; init; }
            public BitmapSource Image { get; init; }
        }

        private static object FindChannelTab(IEnumerable tabs, string target, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return null;

            foreach (object tab in tabs)
            {
                if (tab is null || IsColorCombination(tab))
                    continue;
                if (string.Equals(ReadString(tab, "Target"), target, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(tab, "Filter"), filter, StringComparison.OrdinalIgnoreCase))
                    return tab;
            }
            return null;
        }

        private static object FindColorTab(IEnumerable tabs, string target)
        {
            foreach (object tab in tabs)
            {
                if (tab is not null && IsColorCombination(tab)
                    && string.Equals(ReadString(tab, "Target"), target, StringComparison.OrdinalIgnoreCase))
                    return tab;
            }
            return null;
        }

        private static async Task RefreshTab(object colorTab)
        {
            MethodInfo refresh = colorTab.GetType().GetMethod("Refresh", new[] { typeof(CancellationToken) })
                ?? throw new InvalidOperationException("The installed livestack version has no ColorCombinationTab.Refresh(CancellationToken)");

            // Refresh swallows its own exceptions upstream and simply leaves StackImage untouched
            await (Task)refresh.Invoke(colorTab, new object[] { CancellationToken.None });
        }

        private static ColorCombinationInfo Describe(object colorTab, string red, string green, string blue)
        {
            Type t = colorTab.GetType();
            return new ColorCombinationInfo()
            {
                Target = ReadString(colorTab, "Target"),
                RedFilter = red,
                GreenFilter = green,
                BlueFilter = blue,
                RedStackCount = t.GetProperty("StackCountRed")?.GetValue(colorTab) as int? ?? -1,
                GreenStackCount = t.GetProperty("StackCountGreen")?.GetValue(colorTab) as int? ?? -1,
                BlueStackCount = t.GetProperty("StackCountBlue")?.GetValue(colorTab) as int? ?? -1,
                Image = t.GetProperty("StackImage")?.GetValue(colorTab) as BitmapSource,
            };
        }

        /// <summary>
        /// Creates (or replaces) the colour combination for a target. Throws with a readable
        /// message when a channel cannot be resolved - the caller turns that into a 400.
        /// </summary>
        public static async Task<ColorCombinationInfo> CreateColorCombination(string target, string red, string green, string blue)
        {
            IList tabs = GetTabs();
            List<object> snapshot = SnapshotTabs(tabs);

            object redTab = FindChannelTab(snapshot, target, red);
            object greenTab = FindChannelTab(snapshot, target, green);
            object blueTab = FindChannelTab(snapshot, target, blue);

            if (redTab is null || greenTab is null || blueTab is null)
            {
                var missing = new List<string>();
                if (redTab is null) missing.Add($"red (\"{red}\")");
                if (greenTab is null) missing.Add($"green (\"{green}\")");
                if (blueTab is null) missing.Add($"blue (\"{blue}\")");
                throw new InvalidOperationException($"No stack found for {string.Join(", ", missing)} on target \"{target}\"");
            }

            // OSC channels are extracted from one debayered frame and are already registered to
            // each other. Re-running star alignment across them would misalign a correct stack.
            bool channelsAlreadyAligned = IsOscFilter(red) && IsOscFilter(green) && IsOscFilter(blue);

            // Do not read a stack the stacker is currently writing to
            if (!await WaitUntilUnlocked(snapshot, target, TimeSpan.FromSeconds(60)))
                throw new InvalidOperationException("The stacker is busy with this target - try again in a moment");

            object colorTab = colorCombinationTabCtor.Invoke(new object[]
            {
                AdvancedAPI.Controls.Profile, redTab, greenTab, blueTab, channelsAlreadyAligned
            });

            await RefreshTab(colorTab);

            ColorCombinationInfo info = Describe(colorTab, ReadString(redTab, "Filter"), ReadString(greenTab, "Filter"), ReadString(blueTab, "Filter"));
            if (info.Image is null)
                throw new InvalidOperationException("The colour combination could not be rendered - check the NINA log for details");

            // Swap only once the new tab is known good, so a failed render never leaves the
            // target without the combination it had before
            object existing = FindColorTab(SnapshotTabs(tabs), target);
            if (existing is not null)
                tabs.Remove(existing);
            tabs.Add(colorTab);
            return info;
        }

        private static bool IsOscFilter(string filter)
        {
            return OscFilters.Any(x => string.Equals(x, filter, StringComparison.OrdinalIgnoreCase));
        }

        public static bool RemoveColorCombination(string target)
        {
            IList tabs = GetTabs();
            object colorTab = FindColorTab(SnapshotTabs(tabs), target);
            if (colorTab is null)
                return false;

            tabs.Remove(colorTab);
            return true;
        }

        /// <summary>
        /// Re-renders the existing colour combination of a target. Returns null when there is
        /// none, or when the stacker did not release the channels within the timeout.
        /// </summary>
        public static async Task<ColorCombinationInfo> RefreshColorCombination(string target)
        {
            List<object> snapshot = SnapshotTabs();
            object colorTab = FindColorTab(snapshot, target);
            if (colorTab is null)
                return null;

            // The stacker holds Locked for the whole of StackItem - including the moment it
            // broadcasts the channel update that brought us here. So we must wait for it to
            // finish rather than skip, otherwise every single refresh would be dropped.
            // Same approach the plugin uses itself in RefreshSelectedTabAsync.
            if (!await WaitUntilUnlocked(snapshot, target, TimeSpan.FromSeconds(60)))
                return null; // still busy - the next frame triggers us again anyway

            await RefreshTab(colorTab);
            return Describe(colorTab, null, null, null);
        }

        private static async Task<bool> WaitUntilUnlocked(IEnumerable tabs, string target, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsAnyTabLocked(tabs, target))
                    return true;
                await Task.Delay(25);
            }
            return !IsAnyTabLocked(tabs, target);
        }

        private static bool IsAnyTabLocked(IEnumerable tabs, string target)
        {
            foreach (object tab in tabs)
            {
                if (tab is null || !string.Equals(ReadString(tab, "Target"), target, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (tab.GetType().GetProperty("Locked")?.GetValue(tab) as bool? == true)
                    return true;
            }
            return false;
        }
    }
}
