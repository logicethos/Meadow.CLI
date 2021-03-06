﻿using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading.Tasks;
using Meadow.CLI.DeviceManagement;
using Meadow.CLI.DeviceMonitor;
using Meadow.CLI.Internals.MeadowComms.RecvClasses;
using Meadow.CLI.Internals.Udev;
using MeadowCLI.Hcom;
using static MeadowCLI.DeviceManagement.MeadowFileManager;

namespace MeadowCLI.DeviceManagement
{
    /// <summary>
    /// TODO: put device enumeration and such stuff here.
    /// </summary>
    public static class MeadowDeviceManager
    {
        internal const UInt16 DefaultVS2019DebugPort = 4024;  // Port used by VS 2019

        // Note: While not truly important, it can be noted that size of the s25fl QSPI flash
        // chip's "Page" (i.e. the smallest size it can program) is 256 bytes. By making the
        // maxmimum data block size an even multiple of 256 we insure that each packet received
        // can be immediately written to the s25fl QSPI flash chip.
        internal const int MaxAllowableDataBlock = 512;
        internal const int MaxSizeOfXmitPacket = (MaxAllowableDataBlock + 4) + (MaxAllowableDataBlock / 254);
        internal const int ProtocolHeaderSize = 12;
        internal const int MaxDataSizeInProtocolMsg = MaxAllowableDataBlock - ProtocolHeaderSize;

        //    public static ObservableCollection<MeadowDevice> AttachedDevices = new ObservableCollection<MeadowDevice>();

        public static MeadowSerialDevice CurrentDevice { get; set; } //short cut for now but may be useful

        static HcomMeadowRequestType _meadowRequestType;
        static DebuggingServer debuggingServer;

        static MeadowDeviceManager()
        {
            // TODO: populate the list of attached devices

            // TODO: wire up listeners for device plug and unplug
        }


        public static async Task<MeadowSerialDevice> GetMeadowForConnection(Connection connection)
        {
            Console.WriteLine($"GetMeadowForConnection: Initialize {connection?.USB.DevicePort}");
            
            try
            {
                var meadowSerialDevice = new MeadowSerialDevice(connection, true);
                if (CurrentDevice == null || (CurrentDevice?.connection.Removed ?? true)) CurrentDevice = meadowSerialDevice;
                return meadowSerialDevice;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GetMeadowForConnection Error: {ex.Message}");
                return null;
            }
        }

        [ObsoleteAttribute("This property is obsolete. Use GetMeadowForConnection instead.", false)]
        public static async Task<MeadowSerialDevice> GetMeadowForSerialPort (string serialPort) //, bool verbose = true)
        {

            var connection = new Connection()
            {
                Mode = MeadowMode.MeadowMono,
                USB = new Connection.USB_interface()
                {                
                     DevicePort = serialPort,
                }
            };
            
            return CurrentDevice = new MeadowSerialDevice(connection);
        }



        //we'll move this soon
        public static List<string> FindSerialDevices()
        {
            var devices = new List<string>();

            //Liunx Udev. Only returns matching product
            if (LibudevNative.Instance != null)
            {
                devices.AddRange(Udev.GetUSBDevicePaths("idVendor","2e6a"));  //May need to filter by idProduct someday
            }
            else //Win or Mac
            {
                foreach (var s in SerialPort.GetPortNames())
                {
                    //limit Mac searches to tty.usb*, Windows, try all COM ports
                    //on Mac it's pretty quick to test em all so we could remove this check 
                    if (Environment.OSVersion.Platform != PlatformID.Unix ||
                        s.Contains("tty.usb"))
                    {
                        devices.Add(s);
                    }
                }
            }
            return devices;
        }

        //providing a numeric (0 = none, 1 = info and 2 = debug)
        public static void SetTraceLevel(MeadowSerialDevice meadow, int level)
        {
            if (level < 0 || level > 3)
                throw new System.ArgumentOutOfRangeException(nameof(level), "Trace level must be between 0 & 3 inclusive");

            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_CHANGE_TRACE_LEVEL;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)level);
        }

        public static void ResetMeadow(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_RESET_PRIMARY_MCU;
            meadow.SetImpendingRebootFlag();
            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }

        public static void EnterDfuMode(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_ENTER_DFU_MODE;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void NshEnable(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_ENABLE_DISABLE_NSH;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint) 1);
        }

        public static void MonoDisable(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_MONO_DISABLE;
            meadow.SetImpendingRebootFlag();
            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void MonoEnable(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_MONO_ENABLE;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void MonoRunState(MeadowSerialDevice meadow)
        {
             _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_MONO_RUN_STATE;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void GetDeviceInfo(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_GET_DEVICE_INFORMATION;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void SetDeveloper1(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_DEVELOPER_1;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }
        public static void SetDeveloper2(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_DEVELOPER_2;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }
        public static void SetDeveloper3(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_DEVELOPER_3;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }

        public static void SetDeveloper4(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_DEVELOPER_4;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }

        public static void TraceDisable(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_NO_TRACE_TO_HOST;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void TraceEnable(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_SEND_TRACE_TO_HOST;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void RenewFileSys(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_PART_RENEW_FILE_SYS;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void QspiWrite(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_S25FL_QSPI_WRITE;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }

        public static void QspiRead(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_S25FL_QSPI_READ;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }

        public static void QspiInit(MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_S25FL_QSPI_INIT;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType, (uint)userData);
        }

        // This method is called to sent to Visual Studio debugging to Mono
        public static void ForwardVisualStudioDataToMono(byte[] debuggingData, MeadowSerialDevice meadow, int userData)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_DEBUGGER_MSG;

            new SendTargetData(meadow).BuildAndSendSimpleData(debuggingData, _meadowRequestType, (uint)userData);
        }

        // This method is called to forward from mono debugging to Visual Studio
        public static void ForwardMonoDataToVisualStudio(byte[] debuggerData)
        {
            debuggingServer.SendToVisualStudio(debuggerData);
        }

        // Enter VSDebugging mode.
        public static void VSDebugging(int vsDebugPort)
        {
            // Create an instance of the TCP socket send/receiver class and
            // starts it receiving.
            if (vsDebugPort == 0)
            {
                Console.WriteLine($"With '--VSDebugPort' not found. Assuming Visual Studio 2019 with port {DefaultVS2019DebugPort}");
                vsDebugPort = DefaultVS2019DebugPort;
            }

            debuggingServer = new DebuggingServer(vsDebugPort);
            debuggingServer.StartListening();
        }

        public static void EnterEchoMode(MeadowSerialDevice meadow)
        {
            if (meadow == null)
            {
                Console.WriteLine("No current device");
                return;
            }

            if (!meadow.IsConnected)
            {
                Console.WriteLine("No current serial port or socket");
                return;
            }

         //   meadow.OpenConnection();
        }

        public static void Esp32ReadMac(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_READ_ESP_MAC_ADDRESS;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }

        public static void Esp32Restart(MeadowSerialDevice meadow)
        {
            _meadowRequestType = HcomMeadowRequestType.HCOM_MDOW_REQUEST_RESTART_ESP32;

            new SendTargetData(meadow).SendSimpleCommand(_meadowRequestType);
        }
    }
}