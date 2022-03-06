using HarmonyLib;

namespace Automatics
{
    public static class Reflection
    {
        public static TR InvokeStaticMethod<T, TR>(string methodName, params object[] args) =>
            Traverse.Create<T>().Method(methodName, args).GetValue<TR>(args);

        public static void InvokeStaticMethod<T>(string methodName, params object[] args) =>
            Traverse.Create<T>().Method(methodName, args).GetValue(args);

        public static TR InvokeStaticMethod<T, TR>(string methodName) =>
            Traverse.Create<T>().Method(methodName).GetValue<TR>();

        public static void InvokeStaticMethod<T>(string methodName) =>
            Traverse.Create<T>().Method(methodName).GetValue();

        public static T InvokeMethod<T>(object instance, string methodName, params object[] args) =>
            Traverse.Create(instance).Method(methodName, args).GetValue<T>(args);

        public static void InvokeMethod(object instance, string methodName, params object[] args) =>
            Traverse.Create(instance).Method(methodName, args).GetValue(args);

        public static T InvokeMethod<T>(object instance, string methodName) =>
            Traverse.Create(instance).Method(methodName).GetValue<T>();

        public static void InvokeMethod(object instance, string methodName) =>
            Traverse.Create(instance).Method(methodName).GetValue();

        public static TType GetStaticField<TClass, TType>(string fieldName) =>
            Traverse.Create<TClass>().Field<TType>(fieldName).Value;

        public static TType SetStaticField<TClass, TType>(string fieldName, TType value) =>
            Traverse.Create<TClass>().Field<TType>(fieldName).Value = value;

        public static T GetField<T>(object instance, string fieldName) =>
            Traverse.Create(instance).Field<T>(fieldName).Value;

        public static void SetField<T>(object instance, string fieldName, T value) =>
            Traverse.Create(instance).Field<T>(fieldName).Value = value;
    }
}