﻿using DarkRift;
using DarkRift.Server;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RelayServerPlugin
{
    public class RelayPlugin : Plugin
    {
        public override bool ThreadSafe => true;
        List<Room> rooms = new List<Room>();
        ArrayPool<byte> readBuffers = ArrayPool<byte>.Create(1200, 50);
        List<ushort> pendingConnections = new List<ushort>();
        string authKey = "";

        public override Version Version => new Version("2.0");

        public RelayPlugin(PluginLoadData loadData) : base(loadData)
        {
            ClientManager.ClientConnected += ClientManager_ClientConnected;
            ClientManager.ClientDisconnected += ClientManager_ClientDisconnected;
            authKey = string.IsNullOrEmpty(loadData.Settings["password"]) ? "" : loadData.Settings["password"];
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[DarkReflectiveMirror] Relay server started!");
            Console.WriteLine("[DarkReflectiveMirror] Authentication Key set to: " + authKey);
            Console.ForegroundColor = ConsoleColor.White;
        }

        private void ClientManager_ClientDisconnected(object sender, ClientDisconnectedEventArgs e)
        {
            pendingConnections.RemoveAll(x => x == e.Client.ID);

            e.Client.MessageReceived -= Client_MessageReceived;
            LeaveRoom(e.Client.ID);
        }

        private void ClientManager_ClientConnected(object sender, ClientConnectedEventArgs e)
        {
            e.Client.MessageReceived += Client_MessageReceived;
            pendingConnections.Add(e.Client.ID);
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                using (Message sendAuthRequest = Message.Create((ushort)OpCodes.AuthenticationRequest, writer))
                {
                    e.Client.SendMessage(sendAuthRequest, SendMode.Reliable);
                }
            }

        }

        private void Client_MessageReceived(object sender, MessageReceivedEventArgs e)
        {
            try
            {
                using (Message message = e.GetMessage())
                using (DarkRiftReader reader = message.GetReader())
                {
                    OpCodes opCode = (OpCodes)message.Tag;

                    if(opCode != OpCodes.AuthenticationResponse && pendingConnections.Contains(e.Client.ID))
                    {
                        pendingConnections.Remove(e.Client.ID);
                        LeaveRoom(e.Client.ID);
                        e.Client.Disconnect();
                        return;
                    }

                    switch (opCode)
                    {
                        case OpCodes.UpdateRoomData:
                            string extraData = reader.ReadString();
                            int newMaxPlayers = reader.ReadInt32();
                            UpdateRoomData(e, extraData, newMaxPlayers);
                            break;
                        case OpCodes.AuthenticationResponse:
                            if(reader.ReadString() == authKey)
                            {
                                pendingConnections.Remove(e.Client.ID);
                                using (DarkRiftWriter writer = DarkRiftWriter.Create())
                                {
                                    using (Message sendAuthed = Message.Create((ushort)OpCodes.Authenticated, writer))
                                        e.Client.SendMessage(sendAuthed, SendMode.Reliable);
                                }
                            }
                            else
                            {
                                pendingConnections.Remove(e.Client.ID);
                                LeaveRoom(e.Client.ID);
                                e.Client.Disconnect();
                            }
                            break;
                        case OpCodes.RequestID:
                            SendClientID(e);
                            break;
                        case OpCodes.CreateRoom:
                            CreateRoom(e, reader.ReadInt32(), reader.ReadString(), reader.ReadBoolean(), reader.ReadString(), reader.ReadBoolean(), reader.ReadString());
                            break;
                        case OpCodes.JoinServer:
                            ushort hostID = reader.ReadUInt16();
                            bool useRelay = reader.ReadBoolean();
                            string clientLocalIP = reader.ReadString();
                            JoinRoom(e, hostID, useRelay, clientLocalIP);
                            break;
                        case OpCodes.SendData:
                            int length = reader.ReadInt32();
                            byte[] readBuffer = readBuffers.Rent(length);
                            reader.ReadBytesInto(readBuffer, 0);
                            ProcessData(e, reader, readBuffer, length);
                            break;
                        case OpCodes.LeaveRoom:
                            LeaveRoom(e.Client);
                            break;
                        case OpCodes.KickPlayer:
                            ushort clientID = reader.ReadUInt16();
                            LeaveRoom(clientID, e.Client.ID);
                            break;
                        case OpCodes.RequestServers:
                            SendServerList(e);
                            break;
                    }
                }
            }
            catch
            {
                // Do disconnect/kick maybe later if they do be acting up.
            }
        }

        void UpdateRoomData(MessageReceivedEventArgs e, string extraData, int maxPlayers)
        {
            for(int i = 0; i < rooms.Count; i++)
            {
                if(rooms[i].Host.ID == e.Client.ID)
                {
                    rooms[i] = new Room()
                    {
                        ServerData = extraData,
                        MaxPlayers = maxPlayers,
                        Host = rooms[i].Host,
                        Clients = rooms[i].Clients,
                        ForceRelay = rooms[i].ForceRelay,
                        PublicServer = rooms[i].PublicServer,
                        ServerName = rooms[i].ServerName,
                        ServerLocalIP = rooms[i].ServerLocalIP
                    };
                    return;
                }
            }
        }

        void SendServerList(MessageReceivedEventArgs e)
        {
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                writer.Write(rooms.Where(x => x.PublicServer).Count());

                // Since we are using TCP for reliable, MTU isnt important. If you use a RUDP transport for DarkRift, make sure it has fragmentation or split the server list up into chunks of a few per packet.
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (rooms[i].PublicServer)
                    {
                        writer.Write(rooms[i].ServerName);
                        writer.Write(rooms[i].Clients.Count + 1);
                        writer.Write(rooms[i].MaxPlayers);
                        writer.Write(rooms[i].Host.ID);
                        writer.Write(rooms[i].ServerData);
                    }
                }

                using (Message sendServerList = Message.Create((ushort)OpCodes.ServerListResponse, writer))
                {
                    e.Client.SendMessage(sendServerList, SendMode.Reliable);
                }
            }
        }

        void ProcessData(MessageReceivedEventArgs e, DarkRiftReader reader, byte[] data, int length)
        {
            Room? room = GetRoomForPlayer(e.Client);

            if (room == null)
                return;

            Room playersRoom = room.Value;

            if (playersRoom.Host == e.Client)
            {
                // If the host sent this message then read the ids the host wants this data to be sent to and send it to them.
                var sendingTo = reader.ReadUInt16s().ToList();
                using (DarkRiftWriter writer = DarkRiftWriter.Create())
                {
                    writer.Write(length);
                    writer.Write(data);
                    using (Message sendDataMessage = Message.Create((ushort)OpCodes.GetData, writer))
                    {
                        for (int i = 0; i < playersRoom.Clients.Count; i++)
                        {
                            if (sendingTo.Contains(playersRoom.Clients[i].ID))
                            {
                                sendingTo.Remove(playersRoom.Clients[i].ID);

                                playersRoom.Clients[i].SendMessage(sendDataMessage, e.SendMode);
                            }
                        }
                    }
                }
            }
            else
            {
                // Else we are a client of this room so send the data to the host
                using (DarkRiftWriter writer = DarkRiftWriter.Create())
                {
                    writer.Write(length);
                    writer.Write(data);
                    writer.Write(e.Client.ID);
                    using (Message sendDataMessage = Message.Create((ushort)OpCodes.GetData, writer))
                    {
                        playersRoom.Host.SendMessage(sendDataMessage, e.SendMode);
                    }
                }
            }

            readBuffers.Return(data, true);
        }

        Room? GetRoomForPlayer(IClient client)
        {
            for(int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i].Host == client)
                    return rooms[i];

                if (rooms[i].Clients.Contains(client))
                    return rooms[i];
            }

            return null;
        }

        void JoinRoom(MessageReceivedEventArgs e, ushort hostID, bool useRelay, string localIP)
        {
            LeaveRoom(e.Client);
            
            for(int i = 0; i < rooms.Count; i++)
            {
                if(rooms[i].Host.ID == hostID)
                {
                    if(rooms[i].Clients.Count <= rooms[i].MaxPlayers)
                    {
                        rooms[i].Clients.Add(e.Client);

                        if (rooms[i].ForceRelay || useRelay)
                        {
                            // Tell both the host that this client connected and tell the client we successfully connected to the room
                            using (DarkRiftWriter writer = DarkRiftWriter.Create())
                            {
                                writer.Write(e.Client.ID);
                                using (Message sendConnectedMessage = Message.Create((ushort)OpCodes.ServerJoined, writer))
                                {
                                    rooms[i].Host.SendMessage(sendConnectedMessage, SendMode.Reliable);
                                    e.Client.SendMessage(sendConnectedMessage, SendMode.Reliable);
                                }
                            }
                        }
                        else
                        {
                            // Tell the client the hosts information so they can attempt a direct connection
                            using (DarkRiftWriter writer = DarkRiftWriter.Create())
                            {
                                if (e.Client.RemoteEndPoints.FirstOrDefault().Address.ToString() == rooms[i].Host.RemoteEndPoints.FirstOrDefault().Address.ToString())
                                    writer.Write(localIP == rooms[i].ServerLocalIP ? "127.0.0.1" : rooms[i].ServerLocalIP);
                                else
                                    writer.Write(rooms[i].Host.RemoteEndPoints.FirstOrDefault().Address.ToString());
                                using (Message sendServerInfo = Message.Create((ushort)OpCodes.ServerConnectionData, writer))
                                {
                                    e.Client.SendMessage(sendServerInfo, SendMode.Reliable);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Rooms full, tell the client requesting to join that they have been disconnected.
                        using (DarkRiftWriter writer = DarkRiftWriter.Create())
                        {
                            using (Message sendDisconnectedMessage = Message.Create((ushort)OpCodes.ServerLeft, writer))
                            {
                                for (int x = 0; x < rooms[i].Clients.Count; x++)
                                {
                                    e.Client.SendMessage(sendDisconnectedMessage, SendMode.Reliable);
                                }
                            }
                        }
                    }
                    return;
                }
            }
        }

        void CreateRoom(MessageReceivedEventArgs e, int maxPlayers = 10, string serverName = "My Server", bool isPublic = true, string serverData = "", bool forceRelay = false, string localIP = "127.0.0.1")
        {
            LeaveRoom(e.Client);

            lock (rooms)
            {
                Room room = new Room
                {
                    Host = e.Client,
                    MaxPlayers = maxPlayers,
                    Clients = new List<IClient>(),
                    ServerName = serverName,
                    PublicServer = isPublic,
                    ServerData = serverData,
                    ForceRelay = forceRelay,
                    ServerLocalIP = localIP
                };

                rooms.Add(room);
                // Tell the client that the room was created.
                using (DarkRiftWriter writer = DarkRiftWriter.Create())
                {
                    writer.Write(e.Client.ID);
                    using (Message sendCreatedMessage = Message.Create((ushort)OpCodes.RoomCreated, writer))
                    {
                        e.Client.SendMessage(sendCreatedMessage, SendMode.Reliable);
                    }
                }
            }
        }

        void LeaveRoom(IClient e)
        {
            LeaveRoom(e.ID);
        }

        void LeaveRoom(ushort e, int requiredHostID = -1)
        {
            lock (rooms)
            {
                for (int i = 0; i < rooms.Count; i++)
                {
                    if (rooms[i].Host.ID == e)
                    {
                        // If were the host of a current room, then tell everyone that they have been disconnected and delete room.
                        using (DarkRiftWriter writer = DarkRiftWriter.Create())
                        {
                            using (Message sendHostLeaveMessage = Message.Create((ushort)OpCodes.ServerLeft, writer))
                            {
                                for (int x = 0; x < rooms[i].Clients.Count; x++)
                                {
                                    rooms[i].Clients[x].SendMessage(sendHostLeaveMessage, SendMode.Reliable);
                                }
                            }
                        }
                        rooms[i].Clients.Clear();
                        rooms.RemoveAt(i);
                        i--;
                    }
                    else
                    {
                        if (requiredHostID >= 0)
                        {
                            if (rooms[i].Host.ID != requiredHostID)
                                return;
                        }

                        if (rooms[i].Clients.RemoveAll(x => x.ID == e) > 0)
                        {
                            // If we were in a room, tell the host that we have been disconnected.
                            using (DarkRiftWriter writer = DarkRiftWriter.Create())
                            {
                                writer.Write(e);
                                using (Message sendDisconnectMessage = Message.Create((ushort)OpCodes.PlayerDisconnected, writer))
                                {
                                    rooms[i].Host.SendMessage(sendDisconnectMessage, SendMode.Reliable);
                                }
                            }
                        }
                    }
                }
            }
        }

        void SendClientID(MessageReceivedEventArgs e)
        {
            using (DarkRiftWriter writer = DarkRiftWriter.Create())
            {
                writer.Write(e.Client.ID);
                using (Message sendIDMessage = Message.Create((ushort)OpCodes.GetID, writer))
                    e.Client.SendMessage(sendIDMessage, SendMode.Reliable);
            }
        }
    }

    struct Room
    {
        public IClient Host;
        public string ServerName;
        public string ServerData;
        public string ServerLocalIP;
        public bool PublicServer;
        public bool ForceRelay;
        public int MaxPlayers;
        public List<IClient> Clients;
    }

    enum OpCodes { Default = 0, RequestID = 1, JoinServer = 2, SendData = 3, GetID = 4, ServerJoined = 5, GetData = 6, CreateRoom = 7, ServerLeft = 8, PlayerDisconnected = 9, RoomCreated = 10, LeaveRoom = 11, KickPlayer = 12, AuthenticationRequest = 13, AuthenticationResponse = 14, RequestServers = 15, ServerListResponse = 16, Authenticated = 17, UpdateRoomData = 18, ServerConnectionData = 19 }
}
