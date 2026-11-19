#region "copyright"

/*
    Copyright © 2026 Nico Trost <nico.trost57@gmail.com> and the PI.N.S. contributors

    This file is part of PI 'N' Stars.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Image.ImageAnalysis;
using NINA.Image.ImageData;
using System;

namespace ninaAPI.Utility
{
    /// <summary>
    /// The pattern the API debayers an image with. Casting the profile's bayer pattern to a sensor type turned None into
    /// -1, which is no pattern at all, and Debayer throws for every sensor type but the eight 2x2 patterns: a request for an
    /// OSC camera with the pattern None failed, or logged an error and left the image as it was.
    /// </summary>
    public static class DebayerPattern
    {
        /// <summary>
        /// The pattern to debayer an image with, as NINA's own preview picks it (ImageControlVM.PrepareImage): a pattern set
        /// in the profile as it is; with Auto the image's own pattern with its bayer offsets (the driver's at the capture, or
        /// BAYERPAT, XBAYROFF and YBAYROFF of a saved file), else the connected camera's sensor type with its offsets, the
        /// offsets applied as NINA does (BayerPatternUtility.ApplyOffsets). None treats an OSC camera as monochrome (mono bin
        /// modes), where NINA would fail; so does Auto without a pattern in the image and without a connected camera. Only
        /// the eight 2x2 patterns count as the image's own: a CMYG or LRGB image, which Debayer can't take, falls through
        /// to the camera.
        /// </summary>
        public static SensorType For(BayerPatternEnum profilePattern, ImageMetaData metaData, CameraInfo camera)
        {
            if (profilePattern == BayerPatternEnum.None)
            {
                return SensorType.Monochrome;
            }

            if (profilePattern != BayerPatternEnum.Auto)
            {
                return (SensorType)profilePattern;
            }

            var imageCamera = metaData?.Camera;
            if (imageCamera != null && CanDebayer(imageCamera.SensorType))
            {
                return BayerPatternUtility.ApplyOffsets(imageCamera.SensorType, imageCamera.BayerOffsetX, imageCamera.BayerOffsetY);
            }

            if (camera?.Connected == true)
            {
                return BayerPatternUtility.ApplyOffsets(camera.SensorType, camera.BayerOffsetX, camera.BayerOffsetY);
            }

            return SensorType.Monochrome;
        }

        /// <summary>Whether Debayer can use the sensor type; it throws for monochrome, color, CMYG and LRGB sensors.</summary>
        public static bool CanDebayer(SensorType sensor)
        {
            return sensor is SensorType.RGGB or SensorType.RGBG or SensorType.GRGB or SensorType.GRBG
                or SensorType.GBGR or SensorType.GBRG or SensorType.BGRG or SensorType.BGGR;
        }

        /// <summary>A bayerPattern query value: a sensor type name or number that exists (Enum.Parse accepted any number).</summary>
        public static bool TryParse(string value, out SensorType sensor)
        {
            return Enum.TryParse(value, out sensor) && Enum.IsDefined(sensor);
        }
    }
}
