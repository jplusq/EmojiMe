using System;
using Windows.ApplicationModel.Background;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Devices.Spi;
using Windows.Devices.Gpio;
using Windows.Devices.I2c;
using Q.IoT.Devices.Display;
using Windows.Devices.Enumeration;
using System.Diagnostics;
using Amazon.IotData;
using Amazon.IotData.Model;
using System.IO;
using System.Text.RegularExpressions;
using System.Text;
using Windows.System.Threading;
using System.Globalization;

// The Background Application template is documented at http://go.microsoft.com/fwlink/?LinkID=533884&clcid=0x409

namespace Q.EmojiMe.Display
{
    public sealed class StartupTask : IBackgroundTask
    {
        BackgroundTaskDeferral _deferral;

        //display
        const int SPI_RESET_PIN = 24;
        const int SPI_DATA_COMMAND_PIN = 25;

        SSD1603 _displaySpi;
        GpioPin _pinDataCmd = null;
        GpioPin _pinReset = null;

        SSD1603 _displayI2c;

        //font
        //initialize font
        CanvasTextFormat _fmtBg = new CanvasTextFormat
        {
            FontSize = 42,
            FontFamily = "Segoe UI Emoji",
            LineSpacingMode = CanvasLineSpacingMode.Proportional,
            LineSpacingBaseline = 0.8f
        };

        CanvasTextFormat _fmtSm = new CanvasTextFormat
        {
            FontSize = 18,
            FontFamily = "Segoe UI Emoji",
            LineSpacingMode = CanvasLineSpacingMode.Proportional,
            LineSpacingBaseline = 0.8f
        };


        //AWS IoT Shadow
        private AmazonIotDataClient _client = new AmazonIotDataClient("AKIAIQY2CL3IGCLEHESQ", "2AGk/SrDhtkZoOXpMuNJElRphrBWS08AHQ43+cfb", "https://A3COFSWG6X9L5X.iot.ap-northeast-1.amazonaws.com");
        private const string UPDATE_JSON_TEMPLATE = "{{\"state\":{{\"{0}\": {{\"code\":\"{1}\"}}}}}}";
        private const string CONNECT_JSON_TEMPLATE = "{{\"state\":{{\"reported\": {{\"connected\":{0}}}}}}}";
        private static Regex _deltaRgx = new Regex("\"delta\":{\"code\":\"(?<code>.+)\"}");

        //Timer
        private ThreadPoolTimer _timer;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            // 
            // TODO: Insert code to perform background work
            //
            // If you start any asynchronous methods here, prevent the task
            // from closing prematurely by using BackgroundTaskDeferral as
            // described in http://aka.ms/backgroundtaskdeferral
            //
            _deferral = taskInstance.GetDeferral();
            taskInstance.Canceled += TaskInstance_Canceled;

            //initialize
            InitDisplay().Wait();

            Connect("OLED1", true).Wait();
            Connect("OLED2", true).Wait();

            //
            _timer = ThreadPoolTimer.CreatePeriodicTimer(Polling, TimeSpan.FromMilliseconds(2000));
        }

        private void TaskInstance_Canceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            Connect("OLED1", false).Wait();
            Connect("OLED2", false).Wait();
            // Handle cancellation
            _deferral.Complete();
        }

        #region display delta
        private void Polling(ThreadPoolTimer timer)
        {
            DisplayDelta(0).Wait();
            DisplayDelta(1).Wait();
        }
        private async Task DisplayDelta(int idx)
        {
            Debug.WriteLine("display " + idx);
            string deviceName = idx == 0 ? "OLED1" : "OLED2";
            string emoji = await GetDelat(deviceName);

            if (!string.IsNullOrEmpty(emoji))
            {
                ShowEmoji(idx, emoji);
                await Update(deviceName, emoji);
            }
        }
        #endregion

        #region show text / emoji
        private void ShowEmoji(int idx, string emoji)
        {
            SSD1603 display = idx == 0 ? _displaySpi : _displayI2c;
            CanvasTextFormat fmt = idx == 0 ? _fmtBg : _fmtSm;

            if (display != null && display.State == SSD1603.States.Ready)
            {
                //draw
                using (CanvasDrawingSession ds = display.Render.CreateDrawingSession())
                {
                    ds.Antialiasing = CanvasAntialiasing.Aliased;
                    ds.TextAntialiasing = CanvasTextAntialiasing.Aliased;
                    ds.Clear(SSD1603.BackgroundColor);
                    ds.DrawText(emoji, 0, 0, SSD1603.ForeColor, fmt);
                }

                Debug.WriteLine("display " + emoji);
                display.Display();
            }
        }
        #endregion

        #region IoT shadow
        private async Task Connect(string thingName, bool isConnected)
        {
            try
            {
                string jsonStr = string.Format(CONNECT_JSON_TEMPLATE, isConnected ? "true" : "false");
                UpdateThingShadowRequest req = new UpdateThingShadowRequest();
                req.ThingName = thingName;
                req.Payload = new MemoryStream(Encoding.UTF8.GetBytes(jsonStr));
                UpdateThingShadowResponse res = await _client.UpdateThingShadowAsync(req);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }
        private async Task Update(string thingName, string emoji, bool isDesired = false)
        {
            try
            {
                string jsonStr = string.Format(UPDATE_JSON_TEMPLATE, isDesired ? "desired" : "reported", emoji);

                UpdateThingShadowRequest req = new UpdateThingShadowRequest();
                req.ThingName = thingName;
                req.Payload = new MemoryStream(Encoding.UTF8.GetBytes(jsonStr));
                UpdateThingShadowResponse res = await _client.UpdateThingShadowAsync(req);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        private async Task<string> GetDelat(string thingName)
        {
            try
            {
                GetThingShadowRequest req = new GetThingShadowRequest();
                req.ThingName = thingName;

                GetThingShadowResponse res = await _client.GetThingShadowAsync(req);
                string state = Encoding.UTF8.GetString(res.Payload.ToArray());
                Match m = _deltaRgx.Match(state);
                if (m.Success)
                {
                    return DecodeEncodedNonAsciiCharacters(m.Groups["code"].Value);
                }
                else {
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                return null;
            }
        }

        private static string DecodeEncodedNonAsciiCharacters(string value)
        {
            return Regex.Replace(
                value,
                @"\\u(?<Value>[a-zA-Z0-9]{4})",
                m => {
                    return ((char)int.Parse(m.Groups["Value"].Value, NumberStyles.HexNumber)).ToString();
                });
        }
        #endregion

        #region display
        private async Task InitDisplay()
        {
            try
            {
                await InitGpio();
                var deviceSPI = await InitSPI();
                var deviceI2C = await InitI2C();

                //create SPI display
                SSD1603.SSD1603Configuration cfg_128_64 = SSD1603.CreateConfiguration(Screen.OLED_128_64);
                cfg_128_64.CommonPinConfiguration = SSD1603.CommonPinConfigurationOptions.Alternative;
                cfg_128_64.IsSegmentRemapped = true;
                cfg_128_64.IsCommonScanDirectionRemapped = true;

                _displaySpi = new SSD1603(cfg_128_64, deviceSPI, _pinDataCmd, _pinReset);

                //create I2C display
                _displayI2c = new SSD1603(Screen.OLED_128_32, deviceI2C);


            }
            catch (Exception ex)
            {
                Debug.WriteLine("initialization failed", ex.Message);
                return;
            }
        }

        /// <summary>
        /// initialize GPIO
        /// </summary>
        /// <returns></returns>
        private async Task<bool> InitGpio()
        {
            GpioController IoController = await GpioController.GetDefaultAsync();
            if (IoController == null)
            {
                Debug.WriteLine("GPIO does not exist on the current system");
                return false;
            }


            _pinDataCmd = IoController.OpenPin(SPI_DATA_COMMAND_PIN);
            _pinDataCmd.Write(GpioPinValue.High);
            _pinDataCmd.SetDriveMode(GpioPinDriveMode.Output);

            /* Initialize a pin as output for the hardware Reset line on the display */
            _pinReset = IoController.OpenPin(SPI_RESET_PIN);
            _pinReset.Write(GpioPinValue.High);
            _pinReset.SetDriveMode(GpioPinDriveMode.Output);
            Debug.WriteLine("GPIO initialized");
            return true;
        }

        /// <summary>
        /// initialize SPI device
        /// </summary>
        /// <returns></returns>
        private async Task<SpiDevice> InitSPI()
        {
            /* Uncomment for Raspberry Pi 2 */
            string SPI_CONTROLLER_NAME = "SPI0";  /* For Raspberry Pi 2, use SPI0                             */
            Int32 SPI_CHIP_SELECT_LINE = 0;       /* Line 0 maps to physical pin number 24 on the Rpi2        */


            try
            {
                var settings = new SpiConnectionSettings(SPI_CHIP_SELECT_LINE); /* Create SPI initialization settings                               */
                settings.ClockFrequency = 10000000;                             /* Datasheet specifies maximum SPI clock frequency of 10MHz         */
                settings.Mode = SpiMode.Mode3;                                  /* The display expects an idle-high clock polarity, we use Mode3    
                                                                                 * to set the clock polarity and phase to: CPOL = 1, CPHA = 1         
                                                                                 */

                string spiAqs = SpiDevice.GetDeviceSelector(SPI_CONTROLLER_NAME);       /* Find the selector string for the SPI bus controller          */
                var devicesInfo = await DeviceInformation.FindAllAsync(spiAqs);         /* Find the SPI bus controller device with our selector string  */
                var device = await SpiDevice.FromIdAsync(devicesInfo[0].Id, settings);  /* Create an SpiDevice with our bus controller and SPI settings */
                if (device == null)
                {
                    Debug.WriteLine(string.Format(
                        "Failed to initialize SPI Port on I2C Controller {0}", SPI_CONTROLLER_NAME));
                    return null;
                }
                else
                {
                    Debug.WriteLine(string.Format("SPI Port initialized. chip line={0}, id={1}", device.ConnectionSettings.ChipSelectLine, device.DeviceId));
                    return device;
                }
            }
            /* If initialization fails, display the exception and stop running */
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to initialize SPI Port", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// initialize I2C device
        /// </summary>
        /// <returns></returns>
        private async Task<I2cDevice> InitI2C()
        {
            try
            {
                var settings = new I2cConnectionSettings(SSD1603.I2CSlaveAddress.PrimaryAddress);
                settings.BusSpeed = I2cBusSpeed.FastMode;                       /* 400KHz bus speed */
                string aqs = I2cDevice.GetDeviceSelector();                     /* Get a selector string that will return all I2C controllers on the system */
                var dis = await DeviceInformation.FindAllAsync(aqs);            /* Find the I2C bus controller devices with our selector string             */
                var device = await I2cDevice.FromIdAsync(dis[0].Id, settings);    /* Create an I2cDevice with our selected bus controller and I2C settings    */

                if (device == null)
                {
                    Debug.WriteLine(string.Format(
                        "Failed to initialize I2C Port address={0} on I2C Controller {1}", settings.SlaveAddress, dis[0].Id));
                    return null;
                }
                else
                {
                    Debug.WriteLine(string.Format("I2C Port initialized. address={0}, id={1}", device.ConnectionSettings.SlaveAddress, device.DeviceId));
                    return device;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to initialize I2C Port", ex.Message);
                return null;
            }
        }
        #endregion
    }
}