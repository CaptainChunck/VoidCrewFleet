using System;
using System.Reflection;
using HarmonyLib;

namespace VoidCrewFleet
{
    /// <summary>
    /// Every game member the plugin reaches by name outside of [GamePatch] goes through here, with constant strings:
    /// the offline checker finds these calls in the built DLL and verifies each name against the game's assemblies.
    /// A missing member returns null (and logs once), so callers must handle that. A lookup that fails (for example a
    /// method a game update gave an overload, which makes the name ambiguous) counts as missing: it never throws, because
    /// most lookups run in static initialisers, where an exception would break every hook of the class.
    /// </summary>
    internal static class GameMember
    {
        public static Type Type(string typeName)
        {
            return Find("type " + typeName, "Game type " + typeName, () => AccessTools.TypeByName(typeName));
        }

        public static FieldInfo Field(string typeName, string fieldName)
        {
            Type type = Type(typeName);
            if (type == null) return null;
            return Find("field " + typeName + "." + fieldName, "Game field " + typeName + "." + fieldName, () => AccessTools.Field(type, fieldName));
        }

        public static MethodInfo Method(string typeName, string methodName)
        {
            Type type = Type(typeName);
            if (type == null) return null;
            return Find("method " + typeName + "." + methodName, "Game method " + typeName + "." + methodName, () => AccessTools.Method(type, methodName));
        }

        public static PropertyInfo Property(string typeName, string propertyName)
        {
            Type type = Type(typeName);
            if (type == null) return null;
            return Find("property " + typeName + "." + propertyName, "Game property " + typeName + "." + propertyName, () => AccessTools.Property(type, propertyName));
        }

        // Overloads for types the plugin can name at compile time (typeof); the checker reads the type from the call site.

        public static FieldInfo Field(Type type, string fieldName)
        {
            if (type == null) return null;
            return Find("field " + type.FullName + "." + fieldName, "Game field " + type.FullName + "." + fieldName, () => AccessTools.Field(type, fieldName));
        }

        public static MethodInfo Method(Type type, string methodName)
        {
            if (type == null) return null;
            return Find("method " + type.FullName + "." + methodName, "Game method " + type.FullName + "." + methodName, () => AccessTools.Method(type, methodName));
        }

        public static PropertyInfo Property(Type type, string propertyName)
        {
            if (type == null) return null;
            return Find("property " + type.FullName + "." + propertyName, "Game property " + type.FullName + "." + propertyName, () => AccessTools.Property(type, propertyName));
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

        /// <summary>Runs one lookup: its member, or null (logged once per member) when it is missing or the lookup fails.</summary>
        private static T Find<T>(string key, string what, Func<T> lookup) where T : class
        {
            try
            {
                T member = lookup();
                if (member == null) Log.WarnOnce(key, what + " was not found; the game may have changed.");
                return member;
            }
            catch (Exception e)
            {
                Log.WarnOnce(key, what + " could not be looked up (" + e.GetType().Name + ": " + e.Message + "); the game may have changed.");
                return null;
            }
        }
    }
}
