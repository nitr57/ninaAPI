#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Plugin.Interfaces;
using ninaAPI.Utility;

namespace ninaAPI.WebService.V2
{
    public class LiveStackResponse(bool isMonochrome, int? stackCount, int? redImages, int? greenImages, int? blueImages, string filter, string target, BitmapSource image)
    {
        public bool IsMonochrome { get; set; } = isMonochrome;
        public int? StackCount { get; set; } = stackCount;
        public int? RedStackCount { get; set; } = redImages;
        public int? GreenStackCount { get; set; } = greenImages;
        public int? BlueStackCount { get; set; } = blueImages;
        public string Filter { get; set; } = filter;
        public string Target { get; set; } = target;
        public BitmapSource Image { get; set; } = image;
    }

    public class LiveStackHistory : IDisposable
    {
        // Written from the detached colour refresh task while HTTP handlers read - every access
        // to the list goes through this lock
        private readonly object gate = new object();

        public List<LiveStackResponse> Images { get; set; } = new List<LiveStackResponse>();

        public void AddMono(string filter, int stackCount, string target, BitmapSource image)
        {
            Add(new LiveStackResponse(true, stackCount, null, null, null, filter, target, image));
        }

        public void AddColor(int redImages, int greenImages, int blueImages, string filter, string target, BitmapSource image)
        {
            Add(new LiveStackResponse(false, null, redImages, greenImages, blueImages, filter, target, image));
        }

        public void Add(LiveStackResponse image)
        {
            lock (gate)
            {
                Images.RemoveAll(x => x.Filter == image.Filter && x.Target == image.Target); // Only keep the last stacked image for each filter and target
                Images.Add(image);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                Images.Clear();
                Images = null;
            }
        }

        public BitmapSource GetLast(string filter, string target)
        {
            return Find(filter, target)?.Image;
        }

        public LiveStackResponse Find(string filter, string target)
        {
            lock (gate)
            {
                return Images?.LastOrDefault(x => x.Filter == filter && x.Target == target);
            }
        }

        /// <summary>Copy for callers that need to enumerate, so they cannot see a torn list.</summary>
        public List<LiveStackResponse> Snapshot()
        {
            lock (gate)
            {
                return Images is null ? new List<LiveStackResponse>() : new List<LiveStackResponse>(Images);
            }
        }

        public void Remove(string filter, string target)
        {
            lock (gate)
            {
                Images?.RemoveAll(x => x.Filter == filter && x.Target == target);
            }
        }
    }

    public class StackTabsTargetEntry
    {
        public string Target { get; set; }
        public List<string> Filters { get; set; } = new List<string>();
        public bool HasRgb { get; set; }
        public string SuggestedRed { get; set; }
        public string SuggestedGreen { get; set; }
        public string SuggestedBlue { get; set; }
    }

    public class LiveStackWatcher : INinaWatcher, ISubscriber
    {
        public static LiveStackHistory LiveStackHistory { get; private set; }
        public static string LivestackStatus { get; private set; } = "stopped";

        // One refresh at a time per target, so a burst of channel updates cannot pile up
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> rgbRefreshLocks = new(StringComparer.OrdinalIgnoreCase);

        public async Task OnMessageReceived(IMessage message)
        {
            switch (message.Topic)
            {
                case "Livestack_LivestackDockable_StackUpdateBroadcast":
                    await OnStackUpdateReceived(message);
                    break;

                case "Livestack_LivestackDockable_StatusBroadcast":
                    await OnStatusReceived(message);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Re-renders the colour combination of a target after one of its channels was stacked.
        /// The livestack plugin only refreshes its colour tab when it is selected in the UI,
        /// which never happens headless - so the API drives it instead of patching the plugin.
        /// </summary>
        private static async Task RefreshColorCombination(string target)
        {
            if (string.IsNullOrWhiteSpace(target) || !LivestackBridge.IsAvailable)
                return;

            // With SaveStackedLights on, the plugin refreshes and broadcasts the colour tab on
            // every frame itself - its Color broadcast already reaches our history. Rendering a
            // second time here would duplicate the work and race on the shared tab.
            if (LivestackBridge.PluginRefreshesColorTabItself())
                return;

            SemaphoreSlim gate = rgbRefreshLocks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0))
                return; // a refresh for this target is already running - it will pick up the new data

            try
            {
                LivestackBridge.ColorCombinationInfo info = await LivestackBridge.RefreshColorCombination(target);
                if (info?.Image is null)
                    return;

                LiveStackHistory?.AddColor(info.RedStackCount, info.GreenStackCount, info.BlueStackCount,
                    LivestackBridge.RgbFilter, target, info.Image);

                await WebSocketV2.SendAndAddEvent("STACK-UPDATED", new Dictionary<string, object>()
                {
                    { "Filter", LivestackBridge.RgbFilter },
                    { "Target", target },
                    { "IsMonochrome", false },
                    { "RedStackCount", info.RedStackCount },
                    { "GreenStackCount", info.GreenStackCount },
                    { "BlueStackCount", info.BlueStackCount },
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task OnStatusReceived(IMessage message)
        {
            LivestackStatus = message.Content.ToString();
            await WebSocketV2.SendAndAddEvent("STACK-STATUS", new Dictionary<string, object>()
            {
                { "Status", LivestackStatus },
            });
        }

         public async Task OnStackUpdateReceived(IMessage message)
        {
            // The content object is created by the livestack plugin, so it is our handle on
            // that assembly for the colour combination (see LivestackBridge)
            LivestackBridge.LearnAssemblyFrom(message.Content);

            string filter = message.Content.GetType().GetProperty("Filter").GetValue(message.Content).ToString();
            string target = message.Content.GetType().GetProperty("Target").GetValue(message.Content).ToString();
            bool isMono = (bool)message.Content.GetType().GetProperty("IsMonochrome").GetValue(message.Content);
            int? stackCount = (int?)message.Content.GetType().GetProperty("StackCount").GetValue(message.Content);
            int? redImages = (int?)message.Content.GetType().GetProperty("RedStackCount").GetValue(message.Content);
            int? greenImages = (int?)message.Content.GetType().GetProperty("GreenStackCount").GetValue(message.Content);
            int? blueImages = (int?)message.Content.GetType().GetProperty("BlueStackCount").GetValue(message.Content);

            if (message.Content.GetType().GetProperty("Image").GetValue(message.Content) is BitmapSource image)
            {
                if (isMono)
                {
                    LiveStackHistory.AddMono(filter, stackCount ?? -1, target, image);
                    await WebSocketV2.SendAndAddEvent("STACK-UPDATED", new Dictionary<string, object>()
                    {
                        { "Filter", filter },
                        { "Target", target },
                        { "IsMonochrome", isMono },
                        { "StackCount", stackCount },
                    });

                    // A channel of this target grew - keep its colour combination current.
                    // Detached on purpose: rendering three channels takes a while and waits for
                    // the stacker to release the tabs, which must not hold up message delivery.
                    _ = Task.Run(() => RefreshColorCombination(target));
                }
                else
                {
                    LiveStackHistory.AddColor(redImages ?? -1, greenImages ?? -1, blueImages ?? -1, filter, target, image);
                    await WebSocketV2.SendAndAddEvent("STACK-UPDATED", new Dictionary<string, object>()
                    {
                        { "Filter", filter },
                        { "Target", target },
                        { "IsMonochrome", isMono },
                        { "GreenStackCount", greenImages },
                        { "RedStackCount", redImages },
                        { "BlueStackCount", blueImages },
                    });
                }
            }
        }

        public static void ResetHistory()
        {
            LiveStackHistory = new LiveStackHistory();
            rgbRefreshLocks.Clear();
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.MessageBroker.Subscribe("Livestack_LivestackDockable_StatusBroadcast", this);
            AdvancedAPI.Controls.MessageBroker.Subscribe("Livestack_LivestackDockable_StackUpdateBroadcast", this);
            LiveStackHistory = new LiveStackHistory();
            rgbRefreshLocks.Clear();
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.MessageBroker.Unsubscribe("Livestack_LivestackDockable_StackUpdateBroadcast", this);
            AdvancedAPI.Controls.MessageBroker.Unsubscribe("Livestack_LivestackDockable_StatusBroadcast", this);
            LiveStackHistory.Dispose();
            rgbRefreshLocks.Clear();
        }
    }

    public class LiveStackMessage(Guid correlatedGuid, string topic, object content) : IMessage
    {
        public Guid SenderId => Guid.Parse(AdvancedAPI.PluginId);

        public string Sender => nameof(ninaAPI);

        public DateTimeOffset SentAt => DateTimeOffset.Now;

        public Guid MessageId => Guid.NewGuid();

        public DateTimeOffset? Expiration => null;

        public Guid? CorrelationId => correlatedGuid;

        public int Version => 1;

        public IDictionary<string, object> CustomHeaders => new Dictionary<string, object>();

        public string Topic => topic;

        public object Content => content;
    }

    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/livestack/status")]
        public void LiveStackStatus()
        {
            HttpResponse response = new HttpResponse();

            response.Response = LiveStackWatcher.LivestackStatus;

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/stop")]
        public void LiveStackStop()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                AdvancedAPI.Controls.MessageBroker.Publish(new LiveStackMessage(Guid.NewGuid(), "Livestack_LivestackDockable_StopLiveStack", string.Empty));
                response.Response = "Live stack stopped";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/reset")]
        public async Task LiveStackReset()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                AdvancedAPI.Controls.MessageBroker.Publish(new LiveStackMessage(Guid.NewGuid(), "Livestack_LivestackDockable_ResetLiveStack", string.Empty));
                LiveStackWatcher.LiveStackHistory?.Dispose();
                LiveStackWatcher.ResetHistory();
                await WebSocketV2.SendAndAddEvent("STACK-RESET", new Dictionary<string, object>());
                response.Response = "Live stack reset";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/start")]
        public void LiveStackStart()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                AdvancedAPI.Controls.MessageBroker.Publish(new LiveStackMessage(Guid.NewGuid(), "Livestack_LivestackDockable_StartLiveStack", string.Empty));
                response.Response = "Live stack started";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/image/available")]
        public void LiveStackImageAvailable()
        {
            HttpResponse response = new HttpResponse();

            List<object> images = new List<object>();

            try
            {
                foreach (var image in LiveStackWatcher.LiveStackHistory.Snapshot())
                {
                    images.Add(new { image.Filter, image.Target });
                }
                response.Response = images;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/image/{target}/{filter}")]
        public async Task LiveStackImage(string filter, string target,
            [QueryField] bool resize,
            [QueryField] int quality,
            [QueryField] string size,
            [QueryField] double scale,
            [QueryField] bool stream)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                BitmapSource image = LiveStackWatcher.LiveStackHistory.GetLast(filter, target);
                if (image is null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No image with specified filter and target found", 404));
                }
                else
                {
                    quality = Math.Clamp(quality, -1, 100);
                    if (quality == 0)
                        quality = -1; // quality should be set to -1 for png if omitted

                    if (resize && string.IsNullOrWhiteSpace(size)) // workaround as default parameters are not working
                        size = "640x480";

                    Size sz = Size.Empty;
                    if (resize)
                    {
                        string[] s = size.Split('x');
                        int width = int.Parse(s[0]);
                        int height = int.Parse(s[1]);
                        sz = new Size(width, height);
                    }
                    if (stream)
                    {
                        BitmapEncoder encoder = null;
                        if (scale == 0 && resize)
                        {
                            image = BitmapHelper.ResizeBitmap(image, sz);
                            encoder = BitmapHelper.GetEncoder(image, quality);
                        }
                        if (scale != 0 && resize)
                        {
                            image = BitmapHelper.ScaleBitmap(image, scale);
                            encoder = BitmapHelper.GetEncoder(image, quality);
                        }
                        if (!resize)
                        {
                            image = BitmapHelper.ScaleBitmap(image, 1);
                            encoder = BitmapHelper.GetEncoder(image, quality);
                        }
                        HttpContext.Response.ContentType = quality == -1 ? "image/png" : "image/jpg";
                        using (MemoryStream memory = new MemoryStream())
                        {
                            encoder.Save(memory);
                            await HttpContext.Response.OutputStream.WriteAsync(memory.ToArray());
                            return;
                        }
                    }
                    else
                    {
                        if (scale == 0 && resize)
                            response.Response = BitmapHelper.ResizeAndConvertBitmap(image, sz, quality);
                        if (scale != 0 && resize)
                            response.Response = BitmapHelper.ScaleAndConvertBitmap(image, scale, quality);
                        if (!resize)
                            response.Response = BitmapHelper.ScaleAndConvertBitmap(image, 1, quality);
                    }

                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/rgb/available")]
        public void LiveStackRgbAvailable()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (!LivestackBridge.IsAvailable)
                {
                    response = CoreUtility.CreateErrorTable(new Error(LivestackBridge.UnavailableReason, 400));
                }
                else
                {
                    List<StackTabsTargetEntry> targets = new List<StackTabsTargetEntry>();
                    foreach (var group in LivestackBridge.ListTabs().GroupBy(x => x.Target))
                    {
                        List<string> filters = group.Where(x => !x.IsColorCombination).Select(x => x.Filter).ToList();
                        var suggestion = ColorCombinationDefaults.Suggest(filters);
                        targets.Add(new StackTabsTargetEntry()
                        {
                            Target = group.Key,
                            Filters = filters,
                            HasRgb = group.Any(x => x.IsColorCombination),
                            SuggestedRed = suggestion.Red,
                            SuggestedGreen = suggestion.Green,
                            SuggestedBlue = suggestion.Blue,
                        });
                    }
                    response.Response = targets;
                }
            }
            catch (InvalidOperationException ex)
            {
                // Expected, actionable failures (dockable not created yet, ...)
                response = CoreUtility.CreateErrorTable(new Error(ex.Message, 400));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/rgb/create")]
        public async Task LiveStackRgbCreate(
            [QueryField] string target,
            [QueryField] string red,
            [QueryField] string green,
            [QueryField] string blue)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Target is required", 400));
                }
                else if (!LivestackBridge.IsAvailable)
                {
                    response = CoreUtility.CreateErrorTable(new Error(LivestackBridge.UnavailableReason, 400));
                }
                else
                {
                    // Fill in any omitted channel with the same heuristic the plugin's wizard uses
                    List<string> filters = LivestackBridge.ListTabs()
                        .Where(x => !x.IsColorCombination && string.Equals(x.Target, target, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Filter).ToList();

                    if (filters.Count == 0)
                    {
                        response = CoreUtility.CreateErrorTable(new Error($"No stacks found for target \"{target}\"", 400));
                    }
                    else
                    {
                        var suggestion = ColorCombinationDefaults.Suggest(filters);
                        red = string.IsNullOrWhiteSpace(red) ? suggestion.Red : red;
                        green = string.IsNullOrWhiteSpace(green) ? suggestion.Green : green;
                        blue = string.IsNullOrWhiteSpace(blue) ? suggestion.Blue : blue;

                        LivestackBridge.ColorCombinationInfo info = await LivestackBridge.CreateColorCombination(target, red, green, blue);

                        LiveStackWatcher.LiveStackHistory?.AddColor(info.RedStackCount, info.GreenStackCount, info.BlueStackCount,
                            LivestackBridge.RgbFilter, info.Target, info.Image);

                        await WebSocketV2.SendAndAddEvent("STACK-RGB-CREATED", new Dictionary<string, object>()
                        {
                            { "Target", info.Target },
                            { "RedFilter", info.RedFilter },
                            { "GreenFilter", info.GreenFilter },
                            { "BlueFilter", info.BlueFilter },
                        });

                        response.Response = new
                        {
                            info.Target,
                            info.RedFilter,
                            info.GreenFilter,
                            info.BlueFilter,
                        };
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                // Expected, actionable failures (unknown filter, nothing rendered, ...)
                response = CoreUtility.CreateErrorTable(new Error(ex.Message, 400));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/rgb/delete")]
        public async Task LiveStackRgbDelete([QueryField] string target)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Target is required", 400));
                }
                else if (!LivestackBridge.IsAvailable)
                {
                    response = CoreUtility.CreateErrorTable(new Error(LivestackBridge.UnavailableReason, 400));
                }
                else if (!LivestackBridge.RemoveColorCombination(target))
                {
                    response = CoreUtility.CreateErrorTable(new Error($"No colour combination found for target \"{target}\"", 400));
                }
                else
                {
                    // The RGB stack is gone - do not keep serving the stale image
                    LiveStackWatcher.LiveStackHistory?.Remove(LivestackBridge.RgbFilter, target);
                    await WebSocketV2.SendAndAddEvent("STACK-RGB-REMOVED", new Dictionary<string, object>()
                    {
                        { "Target", target },
                    });
                    response.Response = new { Target = target };
                }
            }
            catch (InvalidOperationException ex)
            {
                response = CoreUtility.CreateErrorTable(new Error(ex.Message, 400));
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/livestack/image/{target}/{filter}/info")]
        public async Task LiveStackImageInfo(string filter, string target)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                LiveStackResponse l = LiveStackWatcher.LiveStackHistory.Find(filter, target);
                if (l is null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No image with specified filter and target found", 404));
                }
                else
                {

                    response.Response = new
                    {
                        IsMonochrome = l.IsMonochrome,
                        StackCount = l.StackCount,
                        RedStackCount = l.RedStackCount,
                        GreenStackCount = l.GreenStackCount,
                        BlueStackCount = l.BlueStackCount,
                        Filter = l.Filter,
                        Target = l.Target,
                    };

                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }
    }
}