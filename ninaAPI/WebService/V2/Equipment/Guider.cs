#region "copyright"

/*
    Copyright © 2025 Christian Palm (christian@palm-family.de)
    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Interfaces;
using NINA.Core.Utility;
using NINA.Equipment.Equipment;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyGuider.PHD2;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.ViewModel.Equipment.Guider;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class GuideInfo
    {
        public GuideInfo(GuiderInfo inf, GuideStep step, string state)
        {
            Connected = inf.Connected;
            Name = inf.Name;
            DisplayName = inf.DisplayName;
            Description = inf.Description;
            DriverInfo = inf.DriverInfo;
            DriverVersion = inf.DriverVersion;
            DeviceId = inf.DeviceId;
            CanClearCalibration = inf.CanClearCalibration;
            CanSetShiftRate = inf.CanSetShiftRate;
            CanGetLockPosition = inf.CanGetLockPosition;
            SupportedActions = inf.SupportedActions;
            RMSError = inf.RMSError;
            PixelScale = inf.PixelScale;
            LastGuideStep = step;

            State = state;
        }

        public bool Connected { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string DriverInfo { get; set; }
        public string DriverVersion { get; set; }
        public string DeviceId { get; set; }
        public bool CanClearCalibration { get; set; }

        public bool CanSetShiftRate { get; set; }

        public bool CanGetLockPosition { get; set; }

        public IList<string> SupportedActions { get; set; }

        public RMSError RMSError { get; set; }

        public double PixelScale { get; set; }
        public GuideStep LastGuideStep { get; set; }
        public string State { get; set; }
    }

    public class GuideStep
    {
        public double RADistanceRaw { get; set; }
        public double DECDistanceRaw { get; set; }

        public double RADuration { get; set; }
        public double DECDuration { get; set; }
    }

    public class GuiderWatcher : INinaWatcher, IGuiderConsumer
    {
        public static GuideStep lastGuideStep { get; set; }

        private readonly Func<object, EventArgs, Task> GuiderConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-CONNECTED");
        private readonly Func<object, EventArgs, Task> GuiderDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-DISCONNECTED");
        private readonly Func<object, EventArgs, Task> GuiderDitherHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-DITHER");
        private readonly Func<object, EventArgs, Task> GuiderStartHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-START");
        private readonly Func<object, EventArgs, Task> GuiderStopHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("GUIDER-STOP");
        private readonly EventHandler<IGuideStep> GuiderGuideEventHandler = (object sender, IGuideStep e) =>
        {
            lastGuideStep = new GuideStep() { DECDistanceRaw = e.DECDistanceRaw, DECDuration = e.DECDuration, RADistanceRaw = e.RADistanceRaw, RADuration = e.RADuration };
        };

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Guider.GuideEvent += GuiderGuideEventHandler;
            AdvancedAPI.Controls.Guider.Connected += GuiderConnectedHandler;
            AdvancedAPI.Controls.Guider.Disconnected += GuiderDisconnectedHandler;
            AdvancedAPI.Controls.Guider.AfterDither += GuiderDitherHandler;
            AdvancedAPI.Controls.Guider.GuidingStarted += GuiderStartHandler;
            AdvancedAPI.Controls.Guider.GuidingStopped += GuiderStopHandler;
            AdvancedAPI.Controls.Guider.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Guider.GuideEvent -= GuiderGuideEventHandler;
            AdvancedAPI.Controls.Guider.Connected -= GuiderConnectedHandler;
            AdvancedAPI.Controls.Guider.Disconnected -= GuiderDisconnectedHandler;
            AdvancedAPI.Controls.Guider.AfterDither -= GuiderDitherHandler;
            AdvancedAPI.Controls.Guider.GuidingStarted -= GuiderStartHandler;
            AdvancedAPI.Controls.Guider.GuidingStopped -= GuiderStopHandler;
            AdvancedAPI.Controls.Guider.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(GuiderInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("GUIDER");
        }

        public void Dispose()
        {
            AdvancedAPI.Controls.Guider.RemoveConsumer(this);
        }
    }

    public partial class ControllerV2
    {
        private static CancellationTokenSource GuideToken;

        [Route(HttpVerbs.Get, "/equipment/guider/info")]
        public void GuiderInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                IGuider g = (IGuider)guider.GetDevice();

                GuideInfo info = new GuideInfo(guider.GetInfo(), GuiderWatcher.lastGuideStep, g?.State);
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/start")]
        public async Task GuiderStart([QueryField] bool calibrate)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    GuideToken?.Cancel();
                    GuideToken = new CancellationTokenSource();
                    await guider.StartGuiding(calibrate, AdvancedAPI.Controls.StatusMediator.GetStatus(), GuideToken.Token);
                    response.Response = "Guiding started";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/stop")]
        public async Task GuiderStop()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    await guider.StopGuiding(CancellationToken.None);
                    response.Response = "Guiding stopped";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/clear-calibration")]
        public async Task ClearCalibration()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    response.Success = await guider.ClearCalibration(CancellationToken.None);
                    response.Response = "Calibration cleared";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/graph")]
        public void GuiderGraph()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                var handlerField = guider.GetType().GetField("handler",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.FlattenHierarchy);

                IGuiderVM gvm = (IGuiderVM)handlerField.GetValue(guider);
                var guiderProperty = gvm.GetType().GetProperty("GuideStepsHistory");

                GuideStepsHistory history = (GuideStepsHistory)guiderProperty.GetValue(gvm);
                response.Response = history;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/graph/clear")]
        public void GuiderGraphClear()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                var handlerField = guider.GetType().GetField("handler",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.FlattenHierarchy);

                IGuiderVM gvm = (IGuiderVM)handlerField.GetValue(guider);
                var guiderProperty = gvm.GetType().GetProperty("GuideStepsHistory");

                GuideStepsHistory history = (GuideStepsHistory)guiderProperty.GetValue(gvm);
                history.Clear();
                response.Response = "Guide graph cleared";
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/get-settings")]
        public void GuiderGetSettings()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Guider.GetDevice();
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider device not available", 409));
                }
                else
                {
                    var properties = device.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var settings = new Dictionary<string, object>();
                    foreach (var prop in properties)
                    {
                        if (prop.CanRead)
                        {
                            try
                            {
                                settings[prop.Name] = prop.GetValue(device);
                            }
                            catch { }
                        }
                    }
                    response.Response = settings;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/set-setting")]
        public void GuiderSetSetting([QueryField] string settingName, [QueryField] string newValue)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (string.IsNullOrEmpty(settingName))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Invalid setting name", 400));
                }
                else if (string.IsNullOrEmpty(newValue))
                {
                    response = CoreUtility.CreateErrorTable(new Error("New value can't be null", 400));
                }
                else
                {
                    var device = AdvancedAPI.Controls.Guider.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Guider device not available", 409));
                    }
                    else
                    {
                        var prop = device.GetType().GetProperty(settingName);
                        if (prop == null)
                        {
                            response = CoreUtility.CreateErrorTable(new Error($"Setting '{settingName}' not found", 400));
                        }
                        else
                        {
                            prop.SetValue(device, newValue.ConvertString(prop.PropertyType));
                            response.Response = "Setting updated";
                        }
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

        [Route(HttpVerbs.Get, "/equipment/guider/dither")]
        public async Task GuiderDither()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IGuiderMediator guider = AdvancedAPI.Controls.Guider;

                if (guider.GetInfo().Connected)
                {
                    response.Success = await guider.Dither(CancellationToken.None);
                    response.Response = "Dither requested";
                }
                else
                {
                    response = CoreUtility.CreateErrorTable(new Error("Guider not connected", 409));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        // The dedicated guide camera for the integrated PHD2 guider (IntegratedGuider). It is a
        // separate ICamera from the imaging camera, selected here and persisted to the profile;
        // the guider resolves and connects it on its own Connect.
        private IDeviceChooserVM GetGuideCameraChooser()
        {
            return (GetDeviceVM("guider").Item1 as GuiderChooserVM)?.GuideCameraChooser;
        }

        [Route(HttpVerbs.Get, "/equipment/guider/integrated/cameras")]
        public async Task IntegratedGuiderCameras()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IDeviceChooserVM chooser = GetGuideCameraChooser();
                if (chooser == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Integrated guider not available", 409));
                }
                else
                {
                    // Populate on first use (the list holds only the "No camera" dummy until scanned).
                    if (chooser.Devices == null || chooser.Devices.Count <= 1)
                    {
                        await chooser.GetEquipment();
                    }
                    response.Response = chooser.Devices;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/integrated/selected-camera")]
        public void IntegratedGuiderSelectedCamera()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                string id = AdvancedAPI.Controls.Profile.ActiveProfile.GuiderSettings.IntegratedGuideCameraId;
                response.Response = new Dictionary<string, object> { { "Id", id ?? string.Empty } };
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/integrated/select-camera")]
        public void IntegratedGuiderSelectCamera([QueryField] string id)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    response = CoreUtility.CreateErrorTable(new Error("Missing camera id", 400));
                }
                else
                {
                    AdvancedAPI.Controls.Profile.ActiveProfile.GuiderSettings.IntegratedGuideCameraId = id;

                    IDeviceChooserVM chooser = GetGuideCameraChooser();
                    IDevice device = chooser?.Devices?.FirstOrDefault(d => d.Id == id);
                    if (device != null)
                    {
                        chooser.SelectedDevice = device;
                    }
                    response.Response = "Guide camera selected";
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/guider/integrated/state")]
        public void IntegratedGuiderStateInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Guider.GetDevice() as IntegratedGuider;
                if (device == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("Integrated guider not active", 409));
                }
                else
                {
                    response.Response = device.GetIntegratedState();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        // Returns the latest guide-camera frame as a base64 image (auto-stretched). The Vue
        // overlay positions the lock box from the integrated/state coordinates.
        [Route(HttpVerbs.Get, "/equipment/guider/integrated/image")]
        public async Task IntegratedGuiderImage([QueryField] int quality, [QueryField] double scale)
        {
            HttpResponse response = new HttpResponse();

            try
            {
                var device = AdvancedAPI.Controls.Guider.GetDevice() as IntegratedGuider;
                var imageData = device?.LastGuideImage;
                if (imageData == null)
                {
                    response = CoreUtility.CreateErrorTable(new Error("No guide image available", 409));
                }
                else
                {
                    var imageSettings = AdvancedAPI.Controls.Profile.ActiveProfile.ImageSettings;
                    var rendered = imageData.RenderImage();
                    rendered = await rendered.Stretch(
                        imageSettings.AutoStretchFactor,
                        imageSettings.BlackClipping,
                        imageSettings.UnlinkedStretch);

                    double s = scale <= 0 ? 1.0 : scale;
                    int q = quality <= 0 ? 90 : quality;
                    response.Response = BitmapHelper.ScaleAndConvertBitmap(rendered.Image, s, q);
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
