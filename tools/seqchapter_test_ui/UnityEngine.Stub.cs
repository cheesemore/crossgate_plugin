// 仅供 Roslyn 编译 SeqChapterTestUi 的桩；运行时由游戏内真 Unity 解析。
namespace UnityEngine
{
    public class Object
    {
        public static void DontDestroyOnLoad(Object target) { }
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b);
        public static bool operator !=(Object a, Object b) => !ReferenceEquals(a, b);
        public override bool Equals(object obj) => ReferenceEquals(this, obj);
        public override int GetHashCode() => base.GetHashCode();
    }

    public class Component : Object { }

    public class Behaviour : Component { }

    public class MonoBehaviour : Behaviour { }

    public class GameObject : Object
    {
        public GameObject(string name) { }

        public T AddComponent<T>() where T : Component => default(T);

        public Component AddComponent(System.Type type) => null;
    }

    public struct Rect
    {
        public Rect(float x, float y, float width, float height) { }
    }

    public class GUI
    {
        public delegate void WindowFunction(int id);

        public static void Box(Rect position, string text) { }

        public static void Label(Rect position, string text) { }

        public static bool Button(Rect position, string text) => false;

        public static Rect Window(int id, Rect clientRect, WindowFunction func, string text) => clientRect;

        public static void DragWindow(Rect position) { }
    }

    public class GUILayout
    {
        public static void Label(string text) { }

        public static bool Button(string text) => false;

        public static bool Button(string text, GUILayoutOption option) => false;

        public static GUILayoutOption Height(float height) => null;
    }

    public sealed class GUILayoutOption { }
}
