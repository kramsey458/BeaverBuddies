namespace UnityEngine
{
    public struct Color { public Color(float r,float g,float b,float a=1){} }
    public enum FontStyle { Bold }
    public enum TextAnchor { MiddleRight }
}
namespace UnityEngine.UIElements
{
    public enum PickingMode { Position, Ignore }
    public enum DisplayStyle { Flex, None }
    public enum LengthUnit { Percent }
    public enum FlexDirection { Row }
    public enum Align { Center }
    public enum WhiteSpace { Normal }
    public enum ScrollViewMode { Vertical }
    public struct Length {public float Value;public Length(float n,LengthUnit unit){Value=n;}}
    public struct Size { public float Value;public static implicit operator Size(float value)=>new(){Value=value};public static implicit operator Size(Length value)=>new(){Value=value.Value};}
    public class Style
    {
        public Size width,maxWidth,maxHeight,height,marginTop,marginLeft,paddingLeft,paddingRight,paddingTop,paddingBottom,borderTopLeftRadius,borderTopRightRadius,borderBottomLeftRadius,borderBottomRightRadius,borderLeftWidth,borderTopWidth,fontSize,flexGrow;
        public UnityEngine.Color backgroundColor,borderLeftColor,color,borderTopColor;public DisplayStyle display;public FlexDirection flexDirection;public Align alignItems;public UnityEngine.FontStyle unityFontStyleAndWeight;public UnityEngine.TextAnchor unityTextAlign;public WhiteSpace whiteSpace;
    }
    public class PointerDownEvent { public bool Stopped;public void StopPropagation()=>Stopped=true; }
    public class WheelEvent {public bool Stopped;public void StopPropagation()=>Stopped=true;}
    public class VisualElement
    {
        public string name,tooltip;public PickingMode pickingMode;public bool focusable;public readonly Style style=new();public VisualElement parent;
        public readonly List<VisualElement> Children=new();public Dictionary<Type,Delegate> Callbacks=new();
        public int childCount=>Children.Count;public VisualElement this[int index]=>Children[index];
        public void Add(VisualElement child){Children.Add(child);child.parent=this;}public void RemoveAt(int i){Children[i].parent=null;Children.RemoveAt(i);}
        public void RemoveFromHierarchy(){parent?.Children.Remove(this);parent=null;}
        public void RegisterCallback<T>(Action<T> callback)=>Callbacks[typeof(T)]=callback;
    }
    public class Label:VisualElement {public string text;public bool enableRichText=true;public Label(string text=""){this.text=text;}}
    public class Button:Label
    {
        public event Action clicked;public Button(Action action=null){if(action!=null)clicked+=action;}public void Click()=>clicked?.Invoke();
    }
    public class ScrollView:VisualElement {public ScrollView(ScrollViewMode mode){} }
}
namespace Timberborn.UILayoutSystem {public class UILayout{public UnityEngine.UIElements.VisualElement Root=new();public void AddTopRight(UnityEngine.UIElements.VisualElement item,int order)=>Root.Add(item);}}
namespace Timberborn.SceneLoading {public class LoadingScreen {public event EventHandler LoadingScreenEnabled;public void Begin()=>LoadingScreenEnabled?.Invoke(this,EventArgs.Empty);}}
namespace Timberborn.SingletonSystem {public interface IPostLoadableSingleton {void PostLoad();}public interface IUpdatableSingleton {void UpdateSingleton();}}
namespace Timberborn.OptionsGame {public class GameOptionsBox{public UnityEngine.UIElements.VisualElement GetPanel()=>new();public void OnUICancelled(){} }}
namespace HarmonyLib {[AttributeUsage(AttributeTargets.Class)]public class HarmonyPatch:Attribute{public HarmonyPatch(Type t,string name){} }}
namespace BeaverBuddies
{
    public interface IResettableSingleton {void Reset();}
    public class RegisteredSingleton {public RegisteredSingleton()=>SingletonManager.Items[GetType()]=this;}
    public static class SingletonManager{public static Dictionary<Type,object> Items=new();public static T GetSingleton<T>()=>Items.TryGetValue(typeof(T),out var v)?(T)v:default;}
    public class ReplayService:RegisteredSingleton {public static TimberNet.TimberNetBase Network;public static bool IsLoaded=true,CompatibilityReady=true,HasReplayFailure;public float TargetSpeed=1;public int TicksSinceLoad;}
    public static class Settings
    {public static bool StatusPanelEnabled=true,StatusPanelCollapsed;public static void SetStatusPanelVisible(bool v)=>StatusPanelEnabled=v;public static void SetStatusPanelCollapsed(bool v)=>StatusPanelCollapsed=v;}
    public static class Plugin{public static void LogWarning(string message)=>Console.WriteLine(message);}
}
namespace BeaverBuddies.IO { }
namespace BeaverBuddies.Connect
{
    public static class SnapshotResyncService {public static bool Active;public static string StatusDescription="Saving snapshot";}
    public static class ButtonInserter
    {public static UnityEngine.UIElements.Button DuplicateOrGetButton(UnityEngine.UIElements.VisualElement root,string previous,string name,Action<UnityEngine.UIElements.Button> init){var b=new UnityEngine.UIElements.Button{name=name};init(b);root.Add(b);return b;}}
}
