using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Steamworks;
using UnityEngine;

namespace BeaverBuddies.Steam
{
    /// <summary>
    /// Runs Steam networking for the whole process. It outlives scenes: a connection is owned by Steam,
    /// not by whichever menu or map happens to be loaded, so the pump lives on a persistent hidden object.
    /// </summary>
    internal static class SteamNet
    {
        static SteamLinkManager manager;
        static int mainThreadId;
        static readonly ConcurrentQueue<Action> mainQueue = new ConcurrentQueue<Action>();
        static readonly List<IDisposable> callbacks = new List<IDisposable>();
        static ESteamNetworkingAvailability lastRelay = (ESteamNetworkingAvailability)int.MinValue;
        static double lastPumpErrorLog = -100;

        public static SteamLinkManager Manager => manager;

        static double Clock() => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

        /// <summary>Call on the game thread once Steam has initialized. Safe to call repeatedly.</summary>
        public static void Initialize()
        {
            if (manager != null) return;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;

            // Valve: call this early if P2P connections are expected, so the relay network is ready when needed.
            SteamNetworkingUtils.InitRelayNetworkAccess();

            manager = new SteamLinkManager(new SteamLinkBackend(), Clock, NameOf);
            callbacks.Add(Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatus));
            callbacks.Add(Callback<SteamRelayNetworkStatus_t>.Create(OnRelayStatus));

            var host = new GameObject("BeaverBuddies_SteamNet") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<SteamNetPump>();
            Application.quitting += OnQuitting;
            Plugin.Log("Steam networking started (relay network access requested).");
        }

        /// <summary>Runs on the game thread: immediately if already there, otherwise on the next frame.</summary>
        public static void RunOnMain(Action action)
        {
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId) action();
            else mainQueue.Enqueue(action);
        }

        internal static void Pump()
        {
            if (manager == null) return;
            while (mainQueue.TryDequeue(out Action action))
            {
                try { action(); }
                catch (Exception e) { Plugin.LogWarning("Steam networking task failed: " + e.Message); }
            }
            try { manager.Pump(); }
            catch (Exception e)
            {
                // Never let a networking problem throw into Unity's update loop every frame.
                double now = Clock();
                if (now - lastPumpErrorLog > 5) { lastPumpErrorLog = now; Plugin.LogWarning("Steam networking pump failed: " + e); }
            }
        }

        static string NameOf(ulong steamId)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
                return string.IsNullOrEmpty(name) ? "Steam player" : name;
            }
            catch (Exception) { return "Steam player"; }
        }

        // Runs when Steam callbacks are pumped, which Timberborn does on the game thread.
        static void OnConnectionStatus(SteamNetConnectionStatusChangedCallback_t change)
        {
            var info = change.m_info;
            // A new arrival on our listen socket starts in "connecting" and must be accepted or closed.
            if (info.m_hListenSocket.m_HSteamListenSocket != 0
                && info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
                && change.m_eOldState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None)
            {
                manager?.OnIncomingConnection(info.m_hListenSocket.m_HSteamListenSocket,
                    change.m_hConn.m_HSteamNetConnection, info.m_identityRemote.GetSteamID64());
            }
        }

        static void OnRelayStatus(SteamRelayNetworkStatus_t status)
        {
            if (status.m_eAvail == lastRelay) return;
            lastRelay = status.m_eAvail;
            Plugin.Log($"Steam relay network: {status.m_eAvail} {status.m_debugMsg}");
        }

        static void OnQuitting()
        {
            try { manager?.Shutdown(); }
            catch (Exception e) { Plugin.LogWarning("Steam networking shutdown failed: " + e.Message); }
        }
    }

    /// <summary>Calls the Steam networking pump once per frame.</summary>
    internal sealed class SteamNetPump : MonoBehaviour
    {
        void Update() => SteamNet.Pump();
    }
}
