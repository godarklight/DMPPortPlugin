using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DarkMultiPlayerServer;
using MessageStream2;

namespace DMPPortPlugin
{
    public class PortPlugin : DMPPlugin
    {
        private const string PORT_SERVER_ADDRESS = "godarklight.info.tm";
        private const int PORT_SERVER_PORT = 9002;
        private Thread checkThread;

        public override void OnServerStart()
        {
            CommandHandler.RegisterCommand("checkport", CheckPortCommand, "Check port forwarding");
        }

        public override void OnServerStop()
        {
            if (checkThread != null)
            {
                checkThread.Abort();
                checkThread = null;
            }
        }

        public void CheckPortCommand(string args)
        {
            if (checkThread != null)
            {
                checkThread.Abort();
                checkThread = null;
            }
            checkThread = new Thread(new ThreadStart(CheckPort));
            checkThread.IsBackground = true;
            checkThread.Start();
        }

        private void CheckPort()
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(PORT_SERVER_ADDRESS);
                foreach (IPAddress address in addresses)
                {
                    TcpClient tcpClient = new TcpClient(address.AddressFamily);
                    DarkLog.Debug("Connecting to " + address);
                    IAsyncResult connectAR = tcpClient.BeginConnect(address, PORT_SERVER_PORT, null, null);

                    if (!connectAR.AsyncWaitHandle.WaitOne(5000))
                    {
                        DarkLog.Normal("Failed to connect to port check server " + address + ", Reason: Timeout");
                        return;
                    }
                    if (!tcpClient.Connected)
                    {
                        DarkLog.Normal("Failed to connect to port check server " + address + ", Reason: Connection refused");
                        return;
                    }
                    tcpClient.EndConnect(connectAR);
                    DarkLog.Debug("Connected to " + address);
                    //Send REQUEST_CHECK message
                    byte[] sendBytes;
                    using (MessageWriter mw = new MessageWriter())
                    {
                        mw.Write<int>(1);
                        mw.Write<int>(4);
                        mw.Write<int>(Settings.settingsStore.port);
                        sendBytes = mw.GetMessageBytes();
                    }
                    IAsyncResult writeAr = tcpClient.GetStream().BeginWrite(sendBytes, 0, sendBytes.Length, null, null);
                    if (!writeAr.AsyncWaitHandle.WaitOne(5000))
                    {
                        DarkLog.Normal("Failed to send REQUEST_CHECK message");
                        return;
                    }
                    tcpClient.GetStream().EndWrite(writeAr);
                    //Receive answer
                    bool receivedCallback = false;
                    byte[] receiveBytes = new byte[8];
                    bool receivingPayload = false;
                    int receiveLeft = receiveBytes.Length;
                    int receiveType = 0;
                    long startTime = DateTime.UtcNow.Ticks;
                    while (!receivedCallback)
                    {
                        IAsyncResult receiveAr = tcpClient.GetStream().BeginRead(receiveBytes, receiveBytes.Length - receiveLeft, receiveLeft, null, null);
                        if (!receiveAr.AsyncWaitHandle.WaitOne(20000))
                        {
                            DarkLog.Normal("Failed to receive REPLY_CHECK message");
                            return;
                        }
                        if ((DateTime.UtcNow.Ticks - startTime) > 3000000000)
                        {
                            DarkLog.Normal("Port check timeout");
                            return;
                        }
                        int thisReceive = tcpClient.GetStream().EndRead(receiveAr);
                        receiveLeft -= thisReceive;
                        if (thisReceive == 0)
                        {
                            Thread.Sleep(10);
                        }
                        if (receiveLeft == 0)
                        {
                            if (!receivingPayload)
                            {
                                using (MessageReader mr = new MessageReader(receiveBytes))
                                {
                                    receiveType = mr.Read<int>();
                                    //Diconnect
                                    if (receiveType == 3)
                                    {
                                        tcpClient.Close();
                                        return;
                                    }
                                    receiveLeft = mr.Read<int>();
                                    if (receiveLeft == 0)
                                    {
                                        //Don't care about heartbeat / no payloads
                                    }
                                    else
                                    {
                                        receivingPayload = true;
                                        receiveBytes = new byte[receiveLeft];
                                    }
                                }
                            }
                            else
                            {
                                receivingPayload = false;
                                if (receiveType == 2)
                                {
                                    receivedCallback = true;
                                    using (MessageReader mr = new MessageReader(receiveBytes))
                                    {
                                        int portChecked = mr.Read<int>();
                                        int replyType = mr.Read<int>();
                                        string replyTypeString = "ERROR";
                                        switch (replyType)
                                        {
                                            case 0:
                                                replyTypeString = "OPEN";
                                                break;
                                            case 1:
                                                replyTypeString = "CLOSED";
                                                break;
                                            case 2:
                                                replyTypeString = "FILTERED";
                                                break;
                                        }
                                        if (address.AddressFamily == AddressFamily.InterNetwork)
                                        {
                                            DarkLog.Normal("IPv4 port check for " + portChecked + " is " + replyTypeString);
                                        }
                                        if (address.AddressFamily == AddressFamily.InterNetworkV6)
                                        {
                                            DarkLog.Normal("IPv6 port check for " + portChecked + " is " + replyTypeString);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    byte[] disconnectBytes;
                    using (MessageWriter mw = new MessageWriter())
                    {
                        mw.Write<int>(3);
                        mw.Write<int>(0);
                        disconnectBytes = mw.GetMessageBytes();
                    }
                    IAsyncResult disconnectAR = tcpClient.GetStream().BeginWrite(disconnectBytes, 0, disconnectBytes.Length, null, null);
                    if (disconnectAR.AsyncWaitHandle.WaitOne(5000))
                    {
                        tcpClient.GetStream().EndWrite(disconnectAR);
                    }
                    tcpClient.Close();
                }
            }
            catch (Exception e)
            {
                DarkLog.Normal("Port check error: " + e.Message);
            }
            checkThread = null;
        }
    }
}

