// UnityEngine.Debug 的最小桩（预览器不链接 UnityEngine）。
namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(System.Exception exception) { }
    }
}
