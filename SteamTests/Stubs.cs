// Only Steam's native boundary and Unity/UI services are doubled.
// Production relay socket, listener, invite service and TimberNet run unchanged.
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
namespace Steamworks
{
 public readonly record struct CSteamID(ulong m_SteamID);
 public readonly record struct HSteamNetConnection(uint Value) { public static HSteamNetConnection Invalid => default; }
 public readonly record struct HSteamListenSocket(uint Value) { public static HSteamListenSocket Invalid => default; }
 public readonly record struct SteamAPICall_t(ulong Value);
 public enum EResult { k_EResultOK, k_EResultFail, k_EResultLimitExceeded }
 public enum ELobbyType { k_ELobbyTypePrivate, k_ELobbyTypeFriendsOnly }
 public enum EChatRoomEnterResponse { k_EChatRoomEnterResponseSuccess=1 }
 public enum ESteamNetworkingConnectionState {
  k_ESteamNetworkingConnectionState_None, k_ESteamNetworkingConnectionState_Connecting,
  k_ESteamNetworkingConnectionState_FindingRoute, k_ESteamNetworkingConnectionState_Connected,
  k_ESteamNetworkingConnectionState_ClosedByPeer, k_ESteamNetworkingConnectionState_ProblemDetectedLocally }
 public struct SteamNetworkingIdentity { CSteamID id; public void SetSteamID(CSteamID value)=>id=value; public CSteamID GetSteamID()=>id; }
 public struct SteamNetworkingConfigValue_t { }
 public struct SteamNetConnectionInfo_t {
  public SteamNetworkingIdentity m_identityRemote; public HSteamListenSocket m_hListenSocket;
  public ESteamNetworkingConnectionState m_eState; public string m_szEndDebug;
 }
 public struct SteamNetConnectionRealTimeStatus_t { public ESteamNetworkingConnectionState m_eState; public int m_cbPendingReliable, m_cbSentUnackedReliable; }
 public struct SteamNetConnectionRealTimeLaneStatus_t { }
 public struct SteamNetConnectionStatusChangedCallback_t { public HSteamNetConnection m_hConn; public SteamNetConnectionInfo_t m_info; }
 public struct LobbyCreated_t { public EResult m_eResult; public ulong m_ulSteamIDLobby; }
 public struct GameLobbyJoinRequested_t { public CSteamID m_steamIDLobby, m_steamIDFriend; }
 public struct LobbyEnter_t { public ulong m_ulSteamIDLobby; public uint m_EChatRoomEnterResponse; }
 public sealed class Callback<T> : IDisposable {
  static readonly List<Callback<T>> all=new(); readonly Action<T> action;
  Callback(Action<T> a){action=a;lock(all)all.Add(this);}
  public static Callback<T> Create(Action<T> a)=>new(a);
  public static void Emit(T t){Callback<T>[] list;lock(all)list=all.ToArray();foreach(var c in list)c.action(t);}
  public void Dispose(){lock(all)all.Remove(this);}
 }
 public sealed class CallResult<T> : IDisposable {
  public static CallResult<T> Last; readonly Action<T,bool> action; bool active;
  CallResult(Action<T,bool> a){action=a;Last=this;} public static CallResult<T> Create(Action<T,bool> a)=>new(a);
  public void Set(SteamAPICall_t call)=>active=true; public bool IsActive()=>active;
  public void Fire(T t,bool failed=false){active=false;action(t,failed);}
  public void Dispose()=>active=false;
 }
 public static class SteamUser { public static CSteamID GetSteamID()=>new(1); }
 public static class SteamFriends {
  public static int Invites; public static string GetFriendPersonaName(CSteamID id)=>"Friend";
  public static void ActivateGameOverlayInviteDialog(CSteamID id)=>Invites++;
 }
 public static class SteamNetworkingUtils { public static int Inits; public static void InitRelayNetworkAccess()=>Inits++; }
 public static class Constants { public const int k_nSteamNetworkingSend_Reliable=8; }
 public static class SteamMatchmaking {
  public static int Creates, Leaves, Joins;
  public static CSteamID Owner=new(1); public static readonly List<CSteamID> Members=new(){new(1),new(2)};
  public static readonly Dictionary<string,string> Data=new();
  public static SteamAPICall_t CreateLobby(ELobbyType type,int max){Creates++;return new(1);}
  public static bool SetLobbyData(CSteamID id,string key,string value){Data[key]=value;return true;}
  public static string GetLobbyData(CSteamID id,string key)=>Data.GetValueOrDefault(key,"");
  public static bool SetLobbyJoinable(CSteamID id,bool joinable)=>true;
  public static void LeaveLobby(CSteamID id)=>Leaves++;
  public static int GetNumLobbyMembers(CSteamID id)=>Members.Count;
  public static CSteamID GetLobbyMemberByIndex(CSteamID id,int index)=>Members[index];
  public static CSteamID GetLobbyOwner(CSteamID id)=>Owner;
  public static SteamAPICall_t JoinLobby(CSteamID id){Joins++;return new(2);}
 }
 public struct SteamNetworkingMessage_t {
  public IntPtr m_pData;public int m_cbSize;
  public static SteamNetworkingMessage_t FromIntPtr(IntPtr p)=>Marshal.PtrToStructure<SteamNetworkingMessage_t>(p);
  public static void Release(IntPtr p){var m=FromIntPtr(p);Marshal.FreeHGlobal(m.m_pData);Marshal.FreeHGlobal(p);SteamNetworkingSockets.Released++;}
 }
 public static class SteamNetworkingSockets {
  public sealed class State {
   public SteamNetConnectionInfo_t Info; public ConcurrentQueue<byte[]> Incoming=new(); public HSteamNetConnection Pair;
  }
  public static readonly ConcurrentDictionary<HSteamNetConnection,State> States=new();
  public static HSteamListenSocket Listener; static int next;
  public static bool ListenFails, AutoPair, FindingRoute;
  public static int Closed, Released, Accepts, PendingBytes, SendAttempts;
  public static volatile bool Backpressure;
  public static EResult SendResult=EResult.k_EResultOK;
  public static byte[] LastSent;public static int LastFlags;
  public static HSteamNetConnection LastConnect;
  public static HSteamListenSocket CreateListenSocketP2P(int port,int n,SteamNetworkingConfigValue_t[] options){
   if(ListenFails)return default; Listener=new((uint)Interlocked.Increment(ref next));return Listener;
  }
  public static HSteamNetConnection New(CSteamID peer,HSteamListenSocket listener=default){
   var id=new HSteamNetConnection((uint)Interlocked.Increment(ref next));var identity=new SteamNetworkingIdentity();identity.SetSteamID(peer);
   States[id]=new(){Info=new(){m_identityRemote=identity,m_hListenSocket=listener,m_eState=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected}};
   return id;
  }
  public static void Signal(HSteamNetConnection id,ESteamNetworkingConnectionState state){
   States[id].Info.m_eState=state;
   Callback<SteamNetConnectionStatusChangedCallback_t>.Emit(new(){m_hConn=id,m_info=States[id].Info});
  }
  public static HSteamNetConnection ConnectP2P(ref SteamNetworkingIdentity identity,int port,int n,SteamNetworkingConfigValue_t[] options){
   var outgoing=New(identity.GetSteamID());LastConnect=outgoing;
   if(FindingRoute)States[outgoing].Info.m_eState=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute;
   if(AutoPair){
    var incoming=New(new(2),Listener);States[incoming].Pair=outgoing;States[outgoing].Pair=incoming;
    Signal(incoming,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting);
    if(States[incoming].Info.m_eState!=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer)
     Signal(incoming,ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected);
   }
   return outgoing;
  }
  public static EResult AcceptConnection(HSteamNetConnection id){Accepts++;return EResult.k_EResultOK;}
  public static bool GetConnectionInfo(HSteamNetConnection id,out SteamNetConnectionInfo_t info){
   if(States.TryGetValue(id,out var s)){info=s.Info;return true;}info=default;return false;
  }
  public static int ReceiveMessagesOnConnection(HSteamNetConnection id,IntPtr[] messages,int n){
   if(!States.TryGetValue(id,out var s))return -1;
   if(!s.Incoming.TryDequeue(out var bytes))return 0;
   var data=Marshal.AllocHGlobal(Math.Max(1,bytes.Length));Marshal.Copy(bytes,0,data,bytes.Length);
   var ptr=Marshal.AllocHGlobal(Marshal.SizeOf<SteamNetworkingMessage_t>());
   Marshal.StructureToPtr(new SteamNetworkingMessage_t{m_pData=data,m_cbSize=bytes.Length},ptr,false);messages[0]=ptr;return 1;
  }
  public static EResult SendMessageToConnection(HSteamNetConnection id,IntPtr data,uint size,int flags,out long message){
   Interlocked.Increment(ref SendAttempts);message=1;if(Backpressure)return EResult.k_EResultLimitExceeded;
   if(SendResult!=EResult.k_EResultOK)return SendResult;
   var bytes=new byte[size];Marshal.Copy(data,bytes,0,(int)size);LastSent=bytes;LastFlags=flags;
   var s=States[id]; if(s.Pair!=default)States[s.Pair].Incoming.Enqueue(bytes);return EResult.k_EResultOK;
  }
  public static EResult FlushMessagesOnConnection(HSteamNetConnection id)=>EResult.k_EResultOK;
  public static EResult GetConnectionRealTimeStatus(HSteamNetConnection id,ref SteamNetConnectionRealTimeStatus_t status,int lanes,ref SteamNetConnectionRealTimeLaneStatus_t lane){
   lane=default;status=new(){m_eState=States[id].Info.m_eState,m_cbPendingReliable=PendingBytes};return EResult.k_EResultOK;
  }
  public static bool CloseConnection(HSteamNetConnection id,int reason,string text,bool linger){
   Closed++;if(States.TryGetValue(id,out var s)){s.Info.m_eState=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;s.Info.m_szEndDebug=text;
    if(s.Pair!=default)States[s.Pair].Info.m_eState=ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer;}return true;
  }
  public static bool CloseListenSocket(HSteamListenSocket id)=>true;
 }
}
namespace BeaverBuddies {
 public static class Plugin { public static void LogWarning(string text){} }
 public class Setting { public void SetValue(string text){} }
 public class Settings { public static bool LobbyJoinable=true,EnableSteam=true; public static string PingDisplayName="Player",DefaultPingPlayerName="Player"; public Setting PingPlayerName=new(); }
}
namespace BeaverBuddies.IO { public static class EventIO { public static bool IsNull=true; } }
namespace BeaverBuddies.Connect {
 public static class SnapshotResyncService { public static bool Active; }
 public class ClientConnectionService {
  public int Connects,Progress;public Steamworks.CSteamID Last;
  public bool TryToConnect(Steamworks.CSteamID id){Connects++;Last=id;return true;}
  public void ShowConnectionMessage(bool value){Progress++;}
 }
}
namespace Timberborn.SingletonSystem { public interface IUpdatableSingleton {void UpdateSingleton();} }
namespace Timberborn.SteamStoreSystem { public class SteamManager {public bool Initialized=true;} }
namespace Timberborn.SteamOverlaySystem { public class SteamOverlayInputBlocker {} }
namespace Timberborn.CoreUI {
 public class EventBus {}
 public class PanelStack { public bool Overlay;public bool IsPanelOnTop(object panel)=>Overlay; }
 public class DialogBoxShower { public int Shown; public string Message; public DialogBoxShower Create()=>this;
  public DialogBoxShower SetMessage(string text){Message=text;return this;} public DialogBoxShower SetDefaultCancelButton()=>this;public void Show()=>Shown++; }
}
