using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Timberborn.BuildingsUI;
using Timberborn.Workshops;
using TimberNet;
using UnityEngine.PlayerLoop;

namespace BeaverBuddies.Steam
{
    public class SteamSocket : ISocketStream, ISteamPacketReceiver
    {
        // Steam can only buffer 1MB at a time, so we need to
        // leave time for that to clear out. Hopefully this is enough.
        // 1 MB per 8 seconds
        const int BYTES_PER_SECOND = 1024 * 1024 / 8;
        // Note this doesn't affect the theoretical issue of multiple
        // events being sent in a single packet - this could split them up
        // but that's fine. So we just choose a moderate size.
        const int MAX_CHUNK_SIZE = 1024 * 8; // 8KB

        public int MaxBytesPerSecond => BYTES_PER_SECOND;
        public int MaxChunkSize => MAX_CHUNK_SIZE;


        public bool Connected { get; private set; }

        public string Name { get; private set; }

        public readonly CSteamID friendID;
        //public readonly CSteamID lobbyID;

        private readonly BlockingCollection<byte[]> readBuffer = new BlockingCollection<byte[]>();
        private byte[] currentPacket;
        private int readOffset = 0;

        private SteamPacketListener packetListener;

        public SteamSocket(CSteamID friendID, bool autoconnect = false)
        {
            this.friendID = friendID;
            Name = SteamFriends.GetFriendPersonaName(friendID);
            Connected = autoconnect;
        }

        public void RegisterSteamPacketListener(SteamPacketListener listener)
        {
            packetListener = listener;
            listener.RegisterSocket(this);
        }

        public Task ConnectAsync()
        {
            // This is the client joining, and this only gets called when
            // we've already joined the lobby. It automatically closes
            // the prior client (I think).
            Connected = true;
            Plugin.Log("SteamSocket requested to connect!");
            return Task.CompletedTask;
        }

        public void Close()
        {
            Connected = false;
            readBuffer.CompleteAdding();
            packetListener?.UnregisterSocket(this);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0 || !Connected) return 0;

            while (currentPacket == null)
            {
                if (!readBuffer.TryTake(out currentPacket, Timeout.Infinite)) return 0;
                readOffset = 0;
                if (currentPacket.Length == 0) currentPacket = null;
            }
            if (!Connected) return 0;
            int bytesToCopy = Math.Min(count, currentPacket.Length - readOffset);
            Array.Copy(currentPacket, readOffset, buffer, offset, bytesToCopy);
            readOffset += bytesToCopy;
            if (readOffset == currentPacket.Length) currentPacket = null;
            return bytesToCopy;
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (!Connected) throw new IOException("Steam socket is closed.");
            if (count > MaxChunkSize)
            {
                throw new IOException($"Attempted to write {buffer.Length} bytes, which exceeds the max chunk size of {MaxChunkSize} bytes.");
            }
            if (offset > 0)
            {
                // Make a copy to avoid modifying the caller's buffer
                byte[] newBuffer = new byte[count];
                Array.Copy(buffer, offset, newBuffer, 0, count);
                buffer = newBuffer;
            }
            Plugin.Log($"SteamSocket sending {count} bytes");
            if (!SteamNetworking.SendP2PPacket(friendID, buffer, (uint)count, EP2PSend.k_EP2PSendReliable))
            {
                throw new IOException("Steam could not queue a reliable packet.");
            }
        }

        public void ReceiveData(byte[] data)
        {
            if (!Connected) return;
            try
            {
                readBuffer.Add(data);
            }
            catch (InvalidOperationException)
            {
                // Close may complete the queue while a packet is being delivered.
            }
        }
    }
}
