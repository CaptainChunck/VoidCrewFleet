using System;
using System.Reflection;
using HarmonyLib;

namespace VoidCrewFleet
{
    /// <summary>
    /// Every game member the plugin reaches by name outside of [GamePatch] goes through here, with constant strings:
    /// the offline checker finds these calls in the built DLL and verifies each name against the game's assemblies.
    /// A missing member returns null (and logs once), so callers must handle that.
    /// </summary>
    internal static class GameMember
    {
        public static Type Type(string typeName)
        {
            Type type = AccessTools.TypeByName(typeName);
            if (type == null) Log.WarnOnce("type " + typeName, "Game type " + typeName + " was not found; the game may have changed.");
            return type;
        }

        public static FieldInfo Field(string typeName, string fieldName)
        {
            Type type = Type(typeName);
            if (type == null) return null;
            FieldInfo field = AccessTools.Field(type, fieldName);
            if (field == null) Log.WarnOnce("field " + typeName + "." + fieldName, "Game field " + typeName + "." + fieldName + " was not found; the game may have changed.");
            return field;
        }

        public static MethodInfo Method(string typeName, string methodName)
        {
            Type type = Type(typeName);
            if (type == null) return null;
            MethodInfo method = AccessTools.Method(type, methodName);
            if (method == null) Log.WarnOnce("method " + typeName + "." + methodName, "Game method " + typeName + "." + methodName + " was not found; the game may have changed.");
            return method;
        }

        public static PropertyInfo Property(string typeName, string propertyName)
        {
            Type type = Type(typeName);
            if (type == null) return null;
            PropertyInfo property = AccessTools.Property(type, propertyName);
            if (property == null) Log.WarnOnce("property " + typeName + "." + propertyName, "Game property " + typeName + "." + propertyName + " was not found; the game may have changed.");
            return property;
        }

        // Overloads for types the plugin can name at compile time (typeof); the checker reads the type from the call site.

        public static FieldInfo Field(Type type, string fieldName)
        {
            FieldInfo field = AccessTools.Field(type, fieldName);
            if (field == null) Log.WarnOnce("field " + type.FullName + "." + fieldName, "Game field " + type.FullName + "." + fieldName + " was not found; the game may have changed.");
            return field;
        }

        public static MethodInfo Method(Type type, string methodName)
        {
            MethodInfo method = AccessTools.Method(type, methodName);
            if (method == null) Log.WarnOnce("method " + type.FullName + "." + methodName, "Game method " + type.FullName + "." + methodName + " was not found; the game may have changed.");
            return method;
        }

        public static PropertyInfo Property(Type type, string propertyName)
        {
            PropertyInfo property = AccessTools.Property(type, propertyName);
            if (property == null) Log.WarnOnce("property " + type.FullName + "." + propertyName, "Game property " + type.FullName + "." + propertyName + " was not found; the game may have changed.");
            return property;
        }

        /// <summary>Reads a field, or returns the fallback when the field is missing or the read fails.</summary>
        public static T Read<T>(FieldInfo field, object instance, T fallback)
        {
            if (field == null) return fallback;
            try
            {
                object value = field.GetValue(instance);
                return value is T ? (T)value : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
