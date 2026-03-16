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
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;
using ninaAPI.Utility;
using System;
using System.Threading.Tasks;

namespace ninaAPI.WebService.V2
{
    public class WeatherWatcher : INinaWatcher, IWeatherDataConsumer
    {
        private readonly Func<object, EventArgs, Task> WeatherConnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("WEATHER-CONNECTED");
        private readonly Func<object, EventArgs, Task> WeatherDisconnectedHandler = async (_, _) => await WebSocketV2.SendAndAddEvent("WEATHER-DISCONNECTED");

        public void Dispose()
        {
            AdvancedAPI.Controls.Weather.RemoveConsumer(this);
        }

        public void StartWatchers()
        {
            AdvancedAPI.Controls.Weather.Connected += WeatherConnectedHandler;
            AdvancedAPI.Controls.Weather.Disconnected += WeatherDisconnectedHandler;
            AdvancedAPI.Controls.Weather.RegisterConsumer(this);
        }

        public void StopWatchers()
        {
            AdvancedAPI.Controls.Weather.Connected -= WeatherConnectedHandler;
            AdvancedAPI.Controls.Weather.Disconnected -= WeatherDisconnectedHandler;
            AdvancedAPI.Controls.Weather.RemoveConsumer(this);
        }

        public async void UpdateDeviceInfo(WeatherDataInfo deviceInfo)
        {
            await WebSocketV2.SendConsumerEvent("WEATHER");
        }
    }

    public partial class ControllerV2
    {
        [Route(HttpVerbs.Get, "/equipment/weather/info")]
        public void WeatherInfo()
        {
            HttpResponse response = new HttpResponse();

            try
            {
                IWeatherDataMediator Weather = AdvancedAPI.Controls.Weather;

                WeatherDataInfo info = Weather.GetInfo();
                response.Response = info;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                response = CoreUtility.CreateErrorTable(CommonErrors.UNKNOWN_ERROR);
            }

            HttpContext.WriteToResponse(response);
        }

        [Route(HttpVerbs.Get, "/equipment/weather/set-setting")]
        public void WeatherSetSetting([QueryField] string settingName, [QueryField] string newValue)
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
                    var device = AdvancedAPI.Controls.Weather.GetDevice();
                    if (device == null)
                    {
                        response = CoreUtility.CreateErrorTable(new Error("Weather device not available", 409));
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
