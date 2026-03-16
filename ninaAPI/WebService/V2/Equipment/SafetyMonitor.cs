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
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using ninaAPI.Utility;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class SafetyWatcher : INinaWatcher
    {
        private readonly Func<object, EventArgs, Task> SafetyConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("SAFETY-CONNECTED");
        private readonly Func<object, EventArgs, Task> SafetyDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("SAFETY-DISCONNECTED");
        private readonly EventHandler<IsSafeEventArgs> SafetyIsSafeChangedHandler = async (_, e) => await WebSocketV2.SendAndAddEvent(
            "SAFETY-CHANGED",
            new Dictionary<string, object>() { { "IsSafe", e.IsSafe } });

        public void StartWatchers()
        {
            AdvancedAPI.Controls.SafetyMonitor.Connected += SafetyConnectedHandler;
            AdvancedAPI.Controls.SafetyMonitor.Disconnected += SafetyDisconnectedHandler;
            AdvancedAPI.Controls.SafetyMonitor.IsSafeChanged += SafetyIsSafeChangedHandler;
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.SafetyMonitor.Connected -= SafetyConnectedHandler;
            AdvancedAPI.Controls.SafetyMonitor.Disconnected -= SafetyDisconnectedHandler;
            AdvancedAPI.Controls.SafetyMonitor.IsSafeChanged -= SafetyIsSafeChangedHandler;
        }
    }

    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/equipment/safetymonitor/info")]
        public void SafetyMonitorInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                ISafetyMonitorMediator safetymonitor = AdvancedAPI.Controls.SafetyMonitor;

                SafetyMonitorInfo info = safetymonitor.GetInfo();
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/safetymonitor/set-setting")]
        public void SafetyMonitorSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.SafetyMonitor.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("SafetyMonitor device not available", 409));
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
    }
}
