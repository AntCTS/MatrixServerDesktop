using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Messaging;
using System.Collections;

namespace MatrixServer
{
    public partial class Form1 : Form
    {
        #region declarations
        public Object qLock;
        public Thread listener;
        public Thread mqpoller;
        public ClientCommands ccmd = new ClientCommands();
        private int ThePort = 10000;
        private int ThePollingInterval = 500;
        private Socket m_mainSocket;
        private static Hashtable _ht = new Hashtable();
        private static Hashtable htSocketList = Hashtable.Synchronized(_ht);
        private ArrayList m_workerSocketList = ArrayList.Synchronized(new ArrayList());
        private int m_clientCount = -1; // start from -1 cause no-one is yet connected...
        private AsyncCallback pfnWorkerCallBack;
        #endregion

        public Form1() { InitializeComponent(); }

        private void Form1_Load(object sender, EventArgs e)
        {
            StartThreads();
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            StopThreads();
        }

        private void StartThreads()
        {
            #region settings init
            try
            {
                if ((ThePort < 1) || (ThePort > 65000))
                {
                    throw new Exception("Invalid port for socket server to start.");
                }
            }
            catch (Exception ex)
            {
                //MessageBox.Show(ex.Message);
                return;
            }
            #endregion
            #region //----Thread - socket listener
            listener = new Thread(new ThreadStart(ListenForRequests));
            listener.IsBackground = true;
            listener.Start();
            #endregion

            #region //----Thread - message-queue polling
            mqpoller = new Thread(new ThreadStart(MQPoller));
            mqpoller.IsBackground = true;
            mqpoller.Start();
            #endregion

        }

        private void StopThreads()
        {
            listener.Abort();
            mqpoller.Abort();
        }

        #region socket server stuff
        public string GetNow()
        {
            string mydatetime = Convert.ToString(DateTime.Now.Year) + "-" + Convert.ToString(DateTime.Now.Month).PadLeft(2, '0') + "-" + Convert.ToString(DateTime.Now.Day).PadLeft(2, '0') + " " + Convert.ToString(DateTime.Now.Hour).PadLeft(2, '0') + ":" + Convert.ToString(DateTime.Now.Minute).PadLeft(2, '0') + ":" + Convert.ToString(DateTime.Now.Second).PadLeft(2, '0');
            return mydatetime;
        }
        private static string GetIP()
        {
            String strHostName = Dns.GetHostName();
            IPHostEntry iphostentry = Dns.GetHostEntry(strHostName);    // Find host by name
            String IPStr = "";                                          // Grab the first IP addresses
            foreach (IPAddress ipaddress in iphostentry.AddressList)
            {
                IPStr = ipaddress.ToString();
                return IPStr;
            }
            return IPStr;
        }
        private void CloseSockets()
        {
            if (m_mainSocket != null)
            {
                m_mainSocket.Close();
            }
            Socket workerSocket;
            for (int i = 0; i < m_workerSocketList.Count; i++)
            {
                workerSocket = (Socket)m_workerSocketList[i];
                if (workerSocket != null)
                {
                    workerSocket.Close();
                    workerSocket = null;
                }
            }
        }
        private void CloseSockets(int clientid)
        {
            try
            {
                Socket workerSocket;
                workerSocket = (Socket)m_workerSocketList[clientid];
                if (workerSocket != null)
                {
                    workerSocket.Close();
                    workerSocket = null;
                }
                m_workerSocketList[clientid] = null;
            }
            catch
            {
            }
        }
        private void IdleCloseSocket(int clientid)
        {
            try
            {
                Socket workerSocket;
                workerSocket = (Socket)m_workerSocketList[clientid];
                if (workerSocket != null)
                {
                    workerSocket.Close();
                    workerSocket = null;
                }
                m_workerSocketList[clientid] = null;
            }
            catch { }
        }
        public void OnClientConnect(IAsyncResult asyn)
        {
            try
            {
                Socket workerSocket = m_mainSocket.EndAccept(asyn);                 // Here we complete/end the BeginAccept() asynchronous call by calling EndAccept() - which returns the reference to a new Socket object
                Interlocked.Increment(ref m_clientCount);                           // Now increment the client count for this client in a thread safe manner
                if(m_workerSocketList.Contains(workerSocket)) {m_workerSocketList.Remove(workerSocket);}
                m_workerSocketList.Add(workerSocket);                               // Add the workerSocket reference to our ArrayList
                ServerWaitForData(workerSocket, m_clientCount, 0, GetNow());        // Let the worker Socket do the further processing for the just connected client
                m_mainSocket.BeginAccept(new AsyncCallback(OnClientConnect), null); // Since the main Socket is now free, it can go back and wait for other clients who are attempting to connect
            }
            catch(Exception ex)
            {
                //MessageBox.Show("OnClientConnect:"+ex.Message);
            }
        }
        public void ServerWaitForData(Socket soc, int clientNumber, int clientId, string dateandTime)
        {
            try
            {
                if (pfnWorkerCallBack == null)
                {
                    pfnWorkerCallBack = new AsyncCallback(ServerOnDataReceived);// call back function which is to be invoked when there is any write activity by the connected client
                }
                ServerSocketPacket theSocPkt = new ServerSocketPacket(soc, clientNumber, clientId, /*timePoint,*/ dateandTime);
                soc.BeginReceive(theSocPkt.dataBuffer, 0, theSocPkt.dataBuffer.Length, SocketFlags.None, pfnWorkerCallBack, theSocPkt);
            }
            catch { }
        }
        public void ServerOnDataReceived(IAsyncResult asyn)
        {
            ServerSocketPacket socketData = (ServerSocketPacket)asyn.AsyncState;
            string clientid = "";
            try
            {
                int iRx = socketData.m_currentSocket.EndReceive(asyn);
                int command = Convert.ToInt32(socketData.dataBuffer[1]);
                string szData = "";
                StringBuilder sb = new StringBuilder();
                for (int j = 0; j < socketData.dataBuffer.Length; j++)
                {
                    sb.Append(Convert.ToString(Convert.ToInt32(socketData.dataBuffer[j])) + " ");
                    //#region logic to cut the zeros from databuffer so log-file is smaller..
                    //if (j < 120)
                    //{
                    //    if (
                    //        (Convert.ToInt64(socketData.dataBuffer[j]) == 0) && (Convert.ToInt64(socketData.dataBuffer[j + 1]) == 0) && (Convert.ToInt64(socketData.dataBuffer[j + 2]) == 0) && (Convert.ToInt32(socketData.dataBuffer[j + 3]) == 0) && (Convert.ToInt32(socketData.dataBuffer[j + 4]) == 0) && (Convert.ToInt32(socketData.dataBuffer[j + 5]) == 0)
                    //      )
                    //    {
                    //        break;
                    //    }
                    //}
                    //#endregion
                }
                szData = sb.ToString();
                if (Convert.ToInt32(socketData.dataBuffer[0]) == 64) // 64=@ do we have a valid header ?
                {
                    #region message recognition section
                    switch (command) // identify command type
                    {
                        case 115: // Command 's' - which means connect , binary 115
                            {
                                #region connect handling
                                clientid = socketData.dataBuffer[2].ToString() + socketData.dataBuffer[3].ToString() + socketData.dataBuffer[4].ToString() + socketData.dataBuffer[5].ToString() + socketData.dataBuffer[6].ToString();
                                socketData.m_clientId = Convert.ToInt32(clientid);
                                try
                                {
                                    if (htSocketList.Contains(socketData.m_clientId))
                                    {
                                        htSocketList.Remove(socketData.m_clientId);
                                    }
                                    htSocketList.Add(socketData.m_clientId, socketData); // save to hash table the clientnumber that defines the socket and the socketdata itself
                                }
                                catch(Exception ex)
                                {
                                    MessageBox.Show("Cannot add:"+ex.Message);
                                }
                                break;
                                #endregion
                            }
                        case 66: // Command 'B' - message broadcast that comes from a client (this is not used yet..)
                            {
                                #region message broadcast
                                clientid = socketData.dataBuffer[2].ToString() + socketData.dataBuffer[3].ToString() + socketData.dataBuffer[4].ToString() + socketData.dataBuffer[5].ToString() + socketData.dataBuffer[6].ToString();
                                string message = System.Text.Encoding.GetEncoding("Windows-1253").GetString(socketData.dataBuffer);
                                message = message.Substring(19, message.IndexOf('^') - 19);
                                // save to db in order to broadcast
                                break;

                                #endregion
                            }
                    }
                    #endregion
                }
                else  // if we detect an invalid command, drop the connection
                {
                    CloseSockets(socketData.m_clientNumber);
                }
                ServerWaitForData(socketData.m_currentSocket, socketData.m_clientNumber, socketData.m_clientId, socketData.m_dateandtime);   // Continue the waiting for data on the Socket
            }
            #region exception handling
            catch (ObjectDisposedException ox)
            {
                //MessageBox.Show(ox.Message);
            }
            catch (SocketException se)
            {
                if (se.ErrorCode == 10054) // Error code for Connection reset by peer
                {
                    m_workerSocketList[socketData.m_clientNumber] = null;                   // Remove the reference to the worker socket of the closed client so that this object will get garbage collected
                }
                else
                {
                }
            }
            #endregion
        }
        public class ServerSocketPacket // buffer capacity is 2097152 bytes
        {
            public ServerSocketPacket(Socket socket, int clientNumber, int clientlId, string dateandtime)// Constructor which takes a Socket,a client number and some other stuff
            {
                if (socket == null) throw new ArgumentNullException("socket");
                m_clientId = clientlId;
                m_currentSocket = socket;
                m_clientNumber = clientNumber;
                m_dateandtime = dateandtime;
            }
            public Socket m_currentSocket;
            public int m_clientNumber;
            public int m_clientId;
            public string m_dateandtime;
            public byte[] dataBuffer = new byte[2097152]; // Buffer to store the data sent by the client
        }
        public void ListenForRequests()
        {
            try
            {
                m_mainSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); // Create the listening socket...
                IPEndPoint ipLocal = new IPEndPoint(IPAddress.Any, ThePort);                                // to this port..
                m_mainSocket.Bind(ipLocal);                                                                 // Bind to local IP Address...
                m_mainSocket.Listen(4);                                                                     // Start listening...
                m_mainSocket.BeginAccept(new AsyncCallback(OnClientConnect), null);                         // Create the call back for any client connections...
            }
            catch (Exception ex)
            {
                MessageBox.Show("ListenForRequests:"+ex.Message);
            }
        }
        bool SendBytesToClient(byte[] bytemsg, int clientNumber, int ClientId)
        {
            bool result;
            try
            {
                ((Socket)m_workerSocketList[clientNumber]).Send(bytemsg);
                result = true;
            }
            catch (Exception ex)
            {                
                //MessageBox.Show("Error SendBytesToClient:"+clientNumber.ToString()+" : "+ex.Message);
                //CloseSockets(clientNumber);
                //htSocketList.Remove(clientNumber);
                result = false; 
            }
            return result;
        }

        public class ClientCommands
        {
            public byte[] BroadcastText(string text, string clientid)
            {
                string clid = clientid.PadLeft(5, '0');
                byte[] _ClientId = { 0, 0, 0, 0, 0 };
                _ClientId[0] = Convert.ToByte(Convert.ToInt16(clid.Substring(0, 1)));
                _ClientId[1] = Convert.ToByte(Convert.ToInt16(clid.Substring(1, 1)));
                _ClientId[2] = Convert.ToByte(Convert.ToInt16(clid.Substring(2, 1)));
                _ClientId[3] = Convert.ToByte(Convert.ToInt16(clid.Substring(3, 1)));
                _ClientId[4] = Convert.ToByte(Convert.ToInt16(clid.Substring(4, 1)));

                byte[] _Text = new byte[text.Length];

                for (int i = 0; i < text.Length; i++)
                {
                    _Text[i] = Convert.ToByte(System.Text.Encoding.GetEncoding("Windows-1253").GetBytes(text[i].ToString().ToCharArray())[0]); // really long eh? but supports greek
                }
                byte[] msg = new byte[_ClientId.Length+_Text.Length];
                _ClientId.CopyTo(msg, 0);
                _Text.CopyTo(msg, 5);
                
                return msg;
            }
        }
        #endregion

        #region messagequeue polling
        public void MQPoller()
        {
            bool msgsent;
            while (true)
            {
                Thread.Sleep(ThePollingInterval);
                try
                {
                    #region traverse the messages in queue and transmit them !
                    qLock = new Object();
                    lock (qLock)
                    {
                        try
                        {
                            MessageQueue queue = new MessageQueue(@".\private$\shadow");
                            if (queue.CanRead) // can we read ?
                            {
                                MessageEnumerator me = queue.GetMessageEnumerator2();
                                me.Reset();
                                while (me.MoveNext()) // flush awayyy.....
                                {
                                    System.Messaging.Message msg = queue.Receive();
                                    msg.Formatter = new BinaryMessageFormatter();
                                    // TRANSMIT
                                    #region traverse clients hashtable and broadcast mq contents to all of them
                                    foreach (int k in htSocketList.Keys)
                                    {
                                        //MessageBox.Show(msg.Body.ToString());
                                        Thread.Sleep(100);
                                        msgsent = SendBytesToClient(ccmd.BroadcastText(msg.Label + "|" + msg.Body, Convert.ToString(k) ), ((ServerSocketPacket)htSocketList[k]).m_clientNumber, ((ServerSocketPacket)htSocketList[k]).m_clientId);
                                    }
                                    #endregion
                    
                                }
                            }
                            queue.Close();
                            queue = null;
                        }
                        catch (Exception ex) 
                        { 
                            //MessageBox.Show("Error Transmitting:"+ex.Message);
                        }
                    }
                    qLock = null;
                    #endregion
                }
                catch (Exception ex) 
                { 
                    //MessageBox.Show("Error Traversing clients:"+ex.Message); 
                }
            }
        }
        #endregion


    }
}
