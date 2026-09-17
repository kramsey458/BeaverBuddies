using Steamworks;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using TimberNet;

namespace BeaverBuddies.Steam
{
    public class SteamPacketListener
    {
        private Dictionary<CSteamID, SteamSocket> sockets = new Dictionary<CSteamID, SteamSocket>();

        public void RegisterSocket(SteamSocket socket)
        {
            lock (sockets) sockets[socket.friendID] = socket;
        }

        public void UnregisterSocket(SteamSocket socket)
        {
            lock (sockets)
            {
                if (sockets.TryGetValue(socket.friendID, out var current) && ReferenceEquals(current, socket))
                    sockets.Remove(socket.friendID);
            }
        }

        public void Update()
        {
            uint messageSize;
            while (SteamNetworking.IsP2PPacketAvailable(out messageSize))
            {
                byte[] buffer = new byte[messageSize];
                uint bytesRead;

                CSteamID remoteSteamID;

                // Read the incoming packet
                if (SteamNetworking.ReadP2PPacket(buffer, messageSize, out bytesRead, out remoteSteamID))
                {
                    // Process the received data
                    //Plugin.Log($"Received {messageSize} bytes from: {remoteSteamID}");
                    //if (buffer.Length == 4)
                    //{
                    //    // This isn't being read correctly - likely an Endian issue
                    //    Plugin.Log("Length: " + BitConverter.ToInt32(buffer, 0));
                    //} else if (buffer.Length < 1000)
                    //{
                    //    Plugin.Log("Data: " + CompressionUtils.Decompress(buffer));
                    //}

                    SteamSocket socket;
                    lock (sockets) sockets.TryGetValue(remoteSteamID, out socket);
                    if (socket != null)
                    {
                        if (bytesRead != buffer.Length) Array.Resize(ref buffer, (int)bytesRead);
                        socket.ReceiveData(buffer);
                    }
                    else
                    {
                        Plugin.LogWarning("Received message from unknown user: " + remoteSteamID);
                    }
                }
                else
                {
                    Plugin.LogWarning("Failed to read packet!");
                }
            }
        }
    }
}
