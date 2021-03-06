﻿using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Meadow.CLI.Internals.MeadowComms.RecvClasses;
using MeadowCLI.DeviceManagement;
using static MeadowCLI.DeviceManagement.MeadowFileManager;

namespace MeadowCLI.Hcom
{
    // For data received due to a CLI request these provide a secondary
    // type of identification. The primary being the protocol request value
    public enum MeadowMessageType
    {
        AppOutput,
        DeviceInfo,
        FileListTitle,
        FileListMember,
        FileListCrcMember,
        Data,
        MeadowTrace,
        SerialReconnect,
        Accepted,
        Concluded,
    }

    public class MeadowMessageEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public MeadowMessageType MessageType { get; private set; }

        public MeadowMessageEventArgs (MeadowMessageType messageType, string message = "")
        {
            Message = message;
            MessageType = messageType;
        }
    }

    public class MeadowSerialDataProcessor
    {   
        //collapse to one and use enum
        public EventHandler<MeadowMessageEventArgs> OnReceiveData;
        public EventHandler OnSocketClosed;
        public EventHandler<string> ConsoleText;
        
        HostCommBuffer _hostCommBuffer;
        RecvFactoryManager _recvFactoryManager;
        readonly SerialPort serialPort;
        readonly Socket socket;

        // It seems that the .Net SerialPort class is not all it could be.
        // To acheive reliable operation some SerialPort class methods must
        // not be used. When receiving, the BaseStream must be used.
        // http://www.sparxeng.com/blog/software/must-use-net-system-io-ports-serialport

        //-------------------------------------------------------------
        // Constructor
        public MeadowSerialDataProcessor()
        {
            _recvFactoryManager = new RecvFactoryManager();
            _hostCommBuffer = new HostCommBuffer();
            _hostCommBuffer.Init(MeadowDeviceManager.MaxSizeOfXmitPacket * 4);

        }

        public MeadowSerialDataProcessor(SerialPort serialPort) : this()
        {
            ConsoleOut($"MeadowSerialDataProcessor: Opening {serialPort.PortName}\n");
            this.serialPort = serialPort;
            var t = ReadSerialPortAsync();
        }

        public MeadowSerialDataProcessor(Socket socket) : this()
        {
            ConsoleOut($"MeadowSerialDataProcessor: Opening {socket.LocalEndPoint}\n");
            this.socket = socket;
            var t = ReadSocketAsync();
            
        }

        //-------------------------------------------------------------
        // All received data handled here
        private async Task ReadSocketAsync()
        {
            byte[] buffer = new byte[MeadowDeviceManager.MaxSizeOfXmitPacket];

            try
            {
                while (true)
                {
                    var segment = new ArraySegment<byte>(buffer);
                    var receivedLength = await socket.ReceiveAsync(segment, SocketFlags.None).ConfigureAwait(false);

                    AddAndProcessData(buffer, receivedLength);

                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (socket?.Connected ?? false) ConsoleOut($"ReadSocketAsync: {ex}\n");
            }
            finally
            {
                OnSocketClosed?.Invoke(this, null);
            }
        }
        

        private async Task ReadSerialPortAsync()
        {
            byte[] buffer = new byte[MeadowDeviceManager.MaxSizeOfXmitPacket];

            try
            {
                while (true)
                {
                    var byteCount = Math.Min(serialPort.BytesToRead, buffer.Length);

                    if (byteCount > 0)
                    {
                        var receivedLength = await serialPort.BaseStream.ReadAsync(buffer, 0, byteCount).ConfigureAwait(false);
                        AddAndProcessData(buffer, receivedLength);
                    }
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            catch (ThreadAbortException ex)
            {
                //ignoring for now until we wire cancelation ...
                //this blocks the thread abort exception when the console app closes
            }
            catch (InvalidOperationException)
            {
                // common if the port is reset/closed (e.g. mono enable/disable) - don't spew confusing info
            }
            catch (Exception ex)
            {
                if (serialPort?.IsOpen ?? false) ConsoleOut($"Exception: {ex} may mean the target connection dropped\n");
            }
            finally
            {
                OnSocketClosed?.Invoke(this, null);
            }
        }


        void AddAndProcessData(byte[] buffer, int availableBytes)
        {
            HcomBufferReturn result;

            while (true)
            {
                // Add these bytes to the circular buffer
                result = _hostCommBuffer.AddBytes(buffer, 0, availableBytes);
                if(result == HcomBufferReturn.HCOM_CIR_BUF_ADD_SUCCESS)
                {
                    break;
                }
                else if(result == HcomBufferReturn.HCOM_CIR_BUF_ADD_WONT_FIT)
                {
                    // Wasn't possible to put these bytes in the buffer. We need to
                    // process a few packets and then retry to add this data
                    result = PullAndProcessAllPackets();
                    if (result == HcomBufferReturn.HCOM_CIR_BUF_GET_FOUND_MSG ||
                        result == HcomBufferReturn.HCOM_CIR_BUF_GET_NONE_FOUND)
                        continue;   // There should be room now for the failed add

                    if(result == HcomBufferReturn.HCOM_CIR_BUF_GET_BUF_NO_ROOM)
                    {
                        // The buffer to receive the message is too small? Probably 
                        // corrupted data in buffer.
                        Debug.Assert(false);
                    }
                }
                else if(result == HcomBufferReturn.HCOM_CIR_BUF_ADD_BAD_ARG)
                {
                    // Something wrong with implemenation
                    Debug.Assert(false);
                }
                else
                {
                    // Undefined return value????
                    Debug.Assert(false);
                }
            }

            result = PullAndProcessAllPackets();

            // Any other response is an error
            Debug.Assert(result == HcomBufferReturn.HCOM_CIR_BUF_GET_FOUND_MSG ||
                result == HcomBufferReturn.HCOM_CIR_BUF_GET_NONE_FOUND);
        }

        HcomBufferReturn PullAndProcessAllPackets()
        {
            byte[] packetBuffer = new byte[MeadowDeviceManager.MaxSizeOfXmitPacket];
            byte[] decodedBuffer = new byte[MeadowDeviceManager.MaxAllowableDataBlock];
            int packetLength;
            HcomBufferReturn result;

            while (true)
            {
                result = _hostCommBuffer.GetNextPacket(packetBuffer, MeadowDeviceManager.MaxAllowableDataBlock, out packetLength);
                if (result == HcomBufferReturn.HCOM_CIR_BUF_GET_NONE_FOUND)
                    break;      // We've emptied buffer of all messages

                if (result == HcomBufferReturn.HCOM_CIR_BUF_GET_BUF_NO_ROOM)
                {
                    // The buffer to receive the message is too small! Perhaps 
                    // corrupted data in buffer.
                    // I don't know why but without the following 2 lines the Debug.Assert will
                    // assert eventhough the following line is not executed?
                    Console.WriteLine($"Need a buffer with {packetLength} bytes, not {MeadowDeviceManager.MaxSizeOfXmitPacket}\n");
                    Thread.Sleep(1000);
                    Debug.Assert(false);
                }

                // Only other possible outcome is success
                Debug.Assert(result == HcomBufferReturn.HCOM_CIR_BUF_GET_FOUND_MSG);

                // It's possible that we may find a series of 0x00 values in the buffer.
                // This is because when the sender is blocked (because this code isn't
                // running) it will attempt to send a single 0x00 before the full message.
                // This allows it to test for a connection. When the connection is
                // unblocked this 0x00 is sent and gets put into the buffer along with
                // any others that were queued along the usb serial pipe line.
                if (packetLength == 1)
                {
                    //ConsoleOut("Throwing out 0x00 from buffer\n");
                    continue;
                }

                int decodedSize = CobsTools.CobsDecoding(packetBuffer, --packetLength, ref decodedBuffer);
                if (decodedSize == 0)
                    continue;

                Debug.Assert(decodedSize <= MeadowDeviceManager.MaxAllowableDataBlock);
             //   Debug.Assert(decodedSize >= HCOM_PROTOCOL_COMMAND_REQUIRED_HEADER_LENGTH);

                // Process the received packet
                if (decodedSize > 0)
                {
                    bool procResult = ParseAndProcessReceivedPacket(decodedBuffer, decodedSize);
                    if (procResult)
                        continue;   // See if there's another packet ready
                }
                break;   // processing errors exit
            }
            return result;
        }

        bool ParseAndProcessReceivedPacket(byte[] receivedMsg, int receivedMsgLen)
        {
            try
            {
                IReceivedMessage processor = _recvFactoryManager.CreateProcessor(receivedMsg, receivedMsgLen);
                if (processor == null)
                    return false;

                if (processor.Execute(receivedMsg, receivedMsgLen))
                {
                    switch(processor.RequestType)
                    {
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_UNDEFINED_REQUEST:
                            ConsoleOut("Request Undefined\n"); // TESTING
                            break;

                            // This set are responses to request issued by this application
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_REJECTED:
                            ConsoleOut("Request Rejected\n"); // TESTING
                            if (!string.IsNullOrEmpty(processor.ToString()))
                            {
                                OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, processor.ToString()));
                            }
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_ACCEPTED:
                            ConsoleOut($"protocol-Request Accepted\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Accepted)); 
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_CONCLUDED:
                            ConsoleOut($"protocol-Request Concluded\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Concluded));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_ERROR:
                            ConsoleOut("Request Error\n"); // TESTING
                            if (!string.IsNullOrEmpty(processor.ToString()))
                            {
                                OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, processor.ToString()));
                            }
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_INFORMATION:
                            ConsoleOut("protocol-Request Information\n"); // TESTING
                            if (!string.IsNullOrEmpty(processor.ToString()))
                                OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.Data, processor.ToString()));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_LIST_HEADER:
                            ConsoleOut("protocol-Request File List Header received\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.FileListTitle, processor.ToString()));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_LIST_MEMBER:
                            ConsoleOut("protocol-Request File List Member received\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.FileListMember, processor.ToString()));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_CRC_MEMBER:
                            ConsoleOut("protocol-Request HCOM_HOST_REQUEST_TEXT_CRC_MEMBER\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.FileListCrcMember, processor.ToString()));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_MONO_MSG:
                            ConsoleOut("protocol-Request HCOM_HOST_REQUEST_TEXT_MONO_MSG\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.AppOutput, processor.ToString()));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_DEVICE_INFO:
                            ConsoleOut("protocol-Request HCOM_HOST_REQUEST_TEXT_DEVICE_INFO\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.DeviceInfo, processor.ToString()));
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_TRACE_MSG:
                            ConsoleOut("protocol-Request HCOM_HOST_REQUEST_TEXT_TRACE_MSG\n"); // TESTING
                            if (!string.IsNullOrEmpty(processor.ToString()))
                            {
                                OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.MeadowTrace, processor.ToString()));
                            }
                            break;
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_TEXT_RECONNECT:
                            ConsoleOut($"Host Serial Reconnect\n"); // TESTING
                            OnReceiveData?.Invoke(this, new MeadowMessageEventArgs(MeadowMessageType.SerialReconnect, null));
                            break;

                        // Debug message from Meadow for Visual Studio
                        case (ushort)HcomHostRequestType.HCOM_HOST_REQUEST_MONO_DEBUGGER_MSG:
                            ConsoleOut($"Debugging message from Meadow for Visual Studio\n"); // TESTING
                            MeadowDeviceManager.ForwardMonoDataToVisualStudio(processor.MessageData);
                            break;
                        default:
                            ConsoleOut($"Unknown message {processor.RequestType}\n");
                            break;

                    }
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                ConsoleOut($"Exception: {ex}\n");
                return false;
            }
        }

        void ConsoleOut(string msg)
        {
#if DEBUG
            ConsoleText?.Invoke(this,msg);
            Console.Write(msg);
#endif
        }

        /*
        // Save for testing in case we suspect data corruption of text
        // The protocol requires the first 12 bytes to be the header. The first 2 are 0x00,
        // the next 10 are binary. After this the rest are ASCII text or binary.
        // Test the message and if it fails it's trashed.
        if(decodedBuffer[0] != 0x00 || decodedBuffer[1] != 0x00)
        {
            ConsoleOut("Corrupted message, first 2 bytes not 0x00\n");
            continue;
        }

        int buffOffset;
        for(buffOffset = MeadowDeviceManager.HCOM_PROTOCOL_COMMAND_REQUIRED_HEADER_LENGTH;
            buffOffset < decodedSize;
            buffOffset++)
        {
            if(decodedBuffer[buffOffset] < 0x20 || decodedBuffer[buffOffset] > 0x7e)
            {
                ConsoleOut($"Corrupted message, non-ascii at offset:{buffOffset} value:{decodedBuffer[buffOffset]}");
                break;
            }
        }

        // Throw away if we found non ASCII where only text should be
        if (buffOffset < decodedSize)
            continue;
        */
    }
}