using BeaverBuddies.Steam;
using Steamworks;
using TimberNet;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Reflection;
using static Steamworks.ESteamNetworkingConnectionState;

int total=0,failures=0;
void Check(bool value,string reason="assertion failed"){if(!value)throw new Exception(reason);}
void Test(string name,Action run){total++;try{run();Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e);}}
void Throws(Action run){try{run();}catch(IOException){return;}throw new Exception("Expected IOException");}
void Wait(Func<bool> predicate,int ms=3000){var w=Stopwatch.StartNew();while(!predicate()){if(w.ElapsedMilliseconds>ms)throw new Exception("Timed out");Thread.Sleep(5);}}
(SteamRelaySocket Socket,HSteamNetConnection Handle) Socket(){var h=SteamNetworkingSockets.New(new(2));return(new(new(2),h),h);}
void Feed(HSteamNetConnection handle,params byte[] bytes)=>SteamNetworkingSockets.States[handle].Incoming.Enqueue(bytes);
SteamListener Host(){var l=new SteamListener();l.Start();CallResult<LobbyCreated_t>.Last.Fire(new(){m_eResult=EResult.k_EResultOK,m_ulSteamIDLobby=123});return l;}
void Cleanup(){SteamNetworkingSockets.Backpressure=false;SteamNetworkingSockets.SendResult=EResult.k_EResultOK;SteamNetworkingSockets.PendingBytes=0;SteamNetworkingSockets.FindingRoute=false;SteamNetworkingSockets.AutoPair=false;}
Test("Relay stream preserves tails across split header/body reads",()=>{
 var(s,h)=Socket();try{Feed(h,1,2,3,4,5,6);var b=new byte[8];Check(s.Read(b,2,2)==2&&b[2]==1&&b[3]==2);Check(s.Read(b,0,3)==3&&b[0]==3&&b[2]==5);Check(s.Read(b,0,8)==1&&b[0]==6);}finally{s.Close();}
});
Test("Zero-length reads do not consume native messages",()=>{
 var(s,h)=Socket();try{Feed(h,9);int before=SteamNetworkingSockets.Released;Check(s.Read(new byte[1],0,0)==0&&SteamNetworkingSockets.Released==before);var b=new byte[1];Check(s.Read(b,0,1)==1&&b[0]==9);}finally{s.Close();}
});
Test("Close wakes blocked native receive polling",()=>{
 var(s,h)=Socket();var read=Task.Run(()=>s.Read(new byte[1],0,1));Thread.Sleep(30);s.Close();Check(read.Wait(1000)&&read.Result==0);
});
Test("Native remote failure is surfaced to TimberNet",()=>{
 var(s,h)=Socket();try{SteamNetworkingSockets.States[h].Info.m_eState=k_ESteamNetworkingConnectionState_ProblemDetectedLocally;Throws(()=>s.Read(new byte[1],0,1));}finally{s.Close();}
});
Test("Oversize native message is rejected and released",()=>{
 var(s,h)=Socket();try{Feed(h,new byte[32769]);int before=SteamNetworkingSockets.Released;Throws(()=>s.Read(new byte[1],0,1));Check(SteamNetworkingSockets.Released==before+1);}finally{s.Close();}
});
Test("Native messages are released after copying, including empty invalid packets",()=>{
 var(s,h)=Socket();try{Feed(h,Array.Empty<byte>());int before=SteamNetworkingSockets.Released;Throws(()=>s.Read(new byte[1],0,1));Check(SteamNetworkingSockets.Released==before+1);}finally{s.Close();}
});
Test("Reliable send honors caller offset/count and uses reliable flags",()=>{
 var(s,h)=Socket();try{s.Write(new byte[]{1,2,3,4},1,2);Check(SteamNetworkingSockets.LastSent.SequenceEqual(new byte[]{2,3}));Check(SteamNetworkingSockets.LastFlags==Constants.k_nSteamNetworkingSend_Reliable);}finally{s.Close();}
});
Test("Congestion waits on worker then sends exactly the requested bytes",()=>{
 var(s,h)=Socket();try{SteamNetworkingSockets.Backpressure=true;var task=Task.Run(()=>s.Write(new byte[]{7,8},0,2));Thread.Sleep(50);Check(!task.IsCompleted);SteamNetworkingSockets.Backpressure=false;Check(task.Wait(1000)&&SteamNetworkingSockets.LastSent.SequenceEqual(new byte[]{7,8}));}finally{s.Close();Cleanup();}
});
Test("Cancel promptly stops a congested sender",()=>{
 var(s,h)=Socket();SteamNetworkingSockets.Backpressure=true;var t=Task.Run(()=>{Throws(()=>s.Write(new byte[]{1},0,1));});Thread.Sleep(30);s.Close();Check(t.Wait(1000));Cleanup();
});
Test("Hard send failures are never reported as successful",()=>{
 var(s,h)=Socket();try{SteamNetworkingSockets.SendResult=EResult.k_EResultFail;Throws(()=>s.Write(new byte[]{1},0,1));}finally{s.Close();Cleanup();}
});
Test("Closing an old connection cannot close its replacement to the same SteamID",()=>{
 var(a,ha)=Socket();var(b,hb)=Socket();a.Close();Check(b.Connected);b.Write(new byte[]{9},0,1);b.Close();
});
Test("Flush waits for Steam's reliable buffer, not just managed queue",()=>{
 var(s,h)=Socket();try{SteamNetworkingSockets.PendingBytes=12;var task=s.FlushAsync();Thread.Sleep(40);Check(!task.IsCompleted);SteamNetworkingSockets.PendingBytes=0;Check(task.Wait(1000));}finally{s.Close();Cleanup();}
});
Test("Relay route discovery waits for connection rather than declaring success immediately",()=>{
 var s=new SteamRelaySocket(new(2));try{SteamNetworkingSockets.FindingRoute=true;var t=s.ConnectAsync();Thread.Sleep(40);Check(!t.IsCompleted&&!s.Connected);SteamNetworkingSockets.States[SteamNetworkingSockets.LastConnect].Info.m_eState=k_ESteamNetworkingConnectionState_Connected;Check(t.Wait(1000)&&s.Connected);}finally{s.Close();Cleanup();}
});
Test("Cancel during route discovery closes the attempt",()=>{
 var s=new SteamRelaySocket(new(2));SteamNetworkingSockets.FindingRoute=true;var task=s.ConnectAsync();s.Close();try{task.GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(IOException){}Cleanup();
});
Test("Invites clicked before lobby creation open when the lobby is ready",()=>{
 int before=SteamFriends.Invites;var l=new SteamListener();l.Start();l.ShowInviteFriendsPanel();Check(SteamFriends.Invites==before);
 CallResult<LobbyCreated_t>.Last.Fire(new(){m_eResult=EResult.k_EResultOK,m_ulSteamIDLobby=123});Check(SteamFriends.Invites==before+1);l.Stop();
});
Test("Failed lobby creation reports status without breaking direct-IP hosting",()=>{
 var l=new SteamListener();l.Start();CallResult<LobbyCreated_t>.Last.Fire(new(){m_eResult=EResult.k_EResultFail});Check(l.Status.Contains("direct IP"));l.Stop();
});
Test("Cancel hosting leaves a lobby that is created after the cancel",()=>{
 var l=new SteamListener();l.Start();var result=CallResult<LobbyCreated_t>.Last;l.Stop();int before=SteamMatchmaking.Leaves;
 result.Fire(new(){m_eResult=EResult.k_EResultOK,m_ulSteamIDLobby=99});Check(SteamMatchmaking.Leaves==before+1);
});
Test("Only lobby members can establish native host connections",()=>{
 var l=Host();try{int before=SteamNetworkingSockets.Accepts;var h=SteamNetworkingSockets.New(new(99),SteamNetworkingSockets.Listener);
 SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connecting);Check(SteamNetworkingSockets.Accepts==before&&!SteamNetworkingSockets.States[h].Info.m_eState.Equals(k_ESteamNetworkingConnectionState_Connecting));}finally{l.Stop();}
});
Test("Duplicate connection callbacks admit one socket only",()=>{
 var l=Host();try{var h=SteamNetworkingSockets.New(new(2),SteamNetworkingSockets.Listener);
 SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connecting);SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connected);SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connected);
 Check(l.AcceptClient().Connected);var next=Task.Run(()=>{try{l.AcceptClient();return false;}catch(IOException){return true;}});Thread.Sleep(40);Check(!next.IsCompleted);l.Stop();Check(next.Wait(1000)&&next.Result);}finally{l.Stop();}
});
Test("Stopping listener wakes a waiting accept worker",()=>{var l=Host();var t=Task.Run(()=>{try{l.AcceptClient();return false;}catch(IOException){return true;}});l.Stop();Check(t.Wait(1000)&&t.Result);});
Test("Failed Steam startup leaves a real TCP listener usable",()=>{
 SteamNetworkingSockets.ListenFails=true;var tcp=new TCPListenerWrapper(0);var multi=new MultiSocketListener(tcp,new SteamListener());try{multi.Start();Check(multi.GetListener<SteamListener>().Status.Contains("Direct IP"));}finally{multi.Stop();SteamNetworkingSockets.ListenFails=false;}
});
Test("Real TimberNet sends a multi-megabyte save over relay without Unity receive pumping",()=>{
 SteamNetworkingSockets.AutoPair=true;var listener=Host();var bytes=new byte[2*1024*1024+19];new Random(7).NextBytes(bytes);
 var server=new TimberServer(listener,()=>Task.FromResult(bytes),null){KeepAliveEnabled=true};
 var client=new TimberClient(new SteamRelaySocket(new(1))){KeepAliveEnabled=true};
 byte[] loaded=null;string error=null;client.OnMapReceived+=b=>loaded=b;client.OnError+=e=>error=e;
 try{server.Start();client.Start();Wait(()=>{server.Update();client.Update();return loaded!=null||error!=null;},8000);Check(error==null,error);Check(loaded.SequenceEqual(bytes));}
 finally{client.Close();server.Close();Cleanup();}
});
Test("Native listener closes unexpected self connections",()=>{var l=Host();try{int before=SteamNetworkingSockets.Accepts;var h=SteamNetworkingSockets.New(new(1),SteamNetworkingSockets.Listener);SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connecting);Check(SteamNetworkingSockets.Accepts==before);}finally{l.Stop();}});
Test("Steam invite errors are deferred until overlay closes",()=>{
 var f=new InviteFixture();f.Panels.Overlay=true;SteamMatchmaking.Owner=new(2);
 f.Invite(456);Callback<LobbyEnter_t>.Emit(new(){m_ulSteamIDLobby=456,m_EChatRoomEnterResponse=2});f.Service.UpdateSingleton();Check(f.Dialogs.Shown==0);
 f.Panels.Overlay=false;f.Service.UpdateSingleton();Check(f.Dialogs.Shown==1&&f.Connection.Connects==0);
});
Test("A matching invite connects once and progress waits for overlay closure",()=>{
 var f=new InviteFixture();SteamMatchmaking.Owner=new(2);SteamMatchmaking.Data["beaverbuddies_protocol"]=SteamListener.Protocol;f.Panels.Overlay=true;
 f.Invite(456);var entered=new LobbyEnter_t{m_ulSteamIDLobby=456,m_EChatRoomEnterResponse=1};Callback<LobbyEnter_t>.Emit(entered);Callback<LobbyEnter_t>.Emit(entered);
 f.Service.UpdateSingleton();Check(f.Connection.Connects==1&&f.Connection.Progress==0);f.Panels.Overlay=false;f.Service.UpdateSingleton();Check(f.Connection.Progress==1);
});
Test("Mismatched Steam protocol explains updating before attempting network join",()=>{
 var f=new InviteFixture();SteamMatchmaking.Owner=new(2);SteamMatchmaking.Data["beaverbuddies_protocol"]="old";f.Invite(456);
 Callback<LobbyEnter_t>.Emit(new(){m_ulSteamIDLobby=456,m_EChatRoomEnterResponse=1});f.Service.UpdateSingleton();Check(f.Connection.Connects==0&&f.Dialogs.Message.Contains("same Preview"));
});
Test("Invite cannot silently replace an active multiplayer session",()=>{
 var f=new InviteFixture();BeaverBuddies.IO.EventIO.IsNull=false;f.Invite(456);f.Service.UpdateSingleton();Check(f.Dialogs.Shown==1&&f.Connection.Connects==0);BeaverBuddies.IO.EventIO.IsNull=true;
});
Test("Stale lobby entry after a newer invite is ignored and left",()=>{
 var f=new InviteFixture();SteamMatchmaking.Owner=new(2);SteamMatchmaking.Data["beaverbuddies_protocol"]=SteamListener.Protocol;f.Invite(456);f.Invite(789);
 int before=SteamMatchmaking.Leaves;Callback<LobbyEnter_t>.Emit(new(){m_ulSteamIDLobby=456,m_EChatRoomEnterResponse=1});Check(f.Connection.Connects==0&&SteamMatchmaking.Leaves==before+1);
 Callback<LobbyEnter_t>.Emit(new(){m_ulSteamIDLobby=789,m_EChatRoomEnterResponse=1});Check(f.Connection.Connects==1);
});

Test("Repeated failed handshakes do not exhaust Steam listener slots",()=>{
 var l=Host();try{for(int i=0;i<12;i++){
 var h=SteamNetworkingSockets.New(new(2),SteamNetworkingSockets.Listener);SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connecting);
 SteamNetworkingSockets.Signal(h,k_ESteamNetworkingConnectionState_Connected);var socket=l.AcceptClient();socket.Close();
 }}finally{l.Stop();}
});
Test("Mixed Steam and real TCP guests share ordered events without compatibility or reconnect handshakes",()=>{
 SteamNetworkingSockets.AutoPair=true;var tcp=new LocalListener();var steam=Host();
 var server=new TimberServer(new MultiSocketListener(tcp,steam),()=>Task.FromResult(new byte[]{1,2,3}),null){KeepAliveEnabled=true};
 TimberClient a=null,b=null;try{
 server.Start();a=new(new SteamRelaySocket(new(1))){KeepAliveEnabled=true};
 b=new(new TCPClientWrapper("127.0.0.1",tcp.Port)){KeepAliveEnabled=true};
 bool mapA=false,mapB=false;a.OnMapReceived+=_=>mapA=true;b.OnMapReceived+=_=>mapB=true;a.Start();b.Start();
 Wait(()=>{server.Update();a.Update();b.Update();return mapA&&mapB&&server.ClientCount==2;},5000);
 for(int i=0;i<100;i++)server.DoUserInitiatedEvent(new JObject{["type"]="Action",["ticksSinceLoad"]=1,["n"]=i});
 var ra=new List<JObject>();var rb=new List<JObject>();
 Wait(()=>{server.Update();a.Update();b.Update();ra.AddRange(a.ReadEvents(1));rb.AddRange(b.ReadEvents(1));return ra.Count==100&&rb.Count==100;},5000);
 Check(ra.Select(x=>(int)x["n"]).SequenceEqual(Enumerable.Range(0,100)));Check(rb.Select(x=>(int)x["n"]).SequenceEqual(Enumerable.Range(0,100)));
 Check(a.Hash==b.Hash&&a.Hash==server.Hash);

 }finally{a?.Close();b?.Close();server.Close();Cleanup();}
});

Console.WriteLine($"{total-failures}/{total} passed (production Steam code; simulated native Steam boundary)");
return failures==0?0:1;

sealed class InviteFixture {
 public BeaverBuddies.Connect.ClientConnectionService Connection=new();
 public Timberborn.CoreUI.PanelStack Panels=new();public Timberborn.CoreUI.DialogBoxShower Dialogs=new();
 public SteamOverlayConnectionService Service;
 public InviteFixture(){
  BeaverBuddies.IO.EventIO.IsNull=true;SteamGuestLobby.Leave();
  Service=new(new(),Connection,new(),Panels,new(),new(),Dialogs);Service.UpdateSingleton();
 }
 public void Invite(ulong id)=>Callback<GameLobbyJoinRequested_t>.Emit(new(){m_steamIDLobby=new(id)});
}


sealed class LocalListener : ISocketListener {
 readonly System.Net.Sockets.TcpListener listener=new(System.Net.IPAddress.Loopback,0);
 public int Port=>((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
 public void Start()=>listener.Start();public void Stop()=>listener.Stop();
 public ISocketStream AcceptClient()=>new TCPClientWrapper(listener.AcceptTcpClient());
}
