using System;
using System.Reflection;

namespace MoreNetworkCapacity.Compatibility
{
    internal delegate object IndexedElementFactory(int index);

    internal delegate object SplitElementFactory(
        int index,
        bool startNode);

    internal static class ArrayCompatibility
    {
        internal static FieldInfo FindField(Type type, string name)
        {
            if (type == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            return type.GetField(
                name,
                BindingFlags.Instance
                | BindingFlags.Static
                | BindingFlags.Public
                | BindingFlags.NonPublic);
        }

        internal static FieldInfo FindBackingField(
            Type type,
            string propertyName)
        {
            if (type == null || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            return FindField(
                type,
                "<" + propertyName + ">k__BackingField");
        }

        internal static object GetStaticInstance(
            Type type,
            string propertyName,
            string fieldName,
            string label)
        {
            try
            {
                if (type == null)
                {
                    return null;
                }

                if (!string.IsNullOrEmpty(propertyName))
                {
                    PropertyInfo property = type.GetProperty(
                        propertyName,
                        BindingFlags.Static
                        | BindingFlags.Public
                        | BindingFlags.NonPublic);
                    if (property != null)
                    {
                        return property.GetValue(null, null);
                    }
                }

                if (!string.IsNullOrEmpty(fieldName))
                {
                    FieldInfo field = FindField(type, fieldName);
                    if (field != null)
                    {
                        return field.GetValue(null);
                    }
                }
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Could not access "
                    + label
                    + " instance: "
                    + exception);
            }

            return null;
        }

        internal static void ExpandDirect(
            FieldInfo field,
            object target,
            int oldExpectedLength,
            int newLength,
            IndexedElementFactory factory,
            bool createIfNull,
            string label)
        {
            if (field == null)
            {
                Logging.Warning(
                    "Could not find compatibility array "
                    + label
                    + ".");
                return;
            }

            try
            {
                Type elementType = field.FieldType.GetElementType();
                if (elementType == null)
                {
                    Logging.Warning(
                        "Compatibility member is not an array: "
                        + label
                        + ".");
                    return;
                }

                Array previous = field.GetValue(target) as Array;
                if (previous == null)
                {
                    if (!createIfNull)
                    {
                        Logging.Info(
                            "Compatibility array is not initialized yet; "
                            + "skipping "
                            + label
                            + ".");
                        return;
                    }

                    Array created =
                        Array.CreateInstance(elementType, newLength);
                    InitializeRange(
                        created,
                        0,
                        newLength,
                        factory);
                    field.SetValue(target, created);
                    Logging.Info(
                        "Created compatibility array "
                        + label
                        + " at length "
                        + newLength
                        + ".");
                    return;
                }

                if (previous.Length == newLength)
                {
                    return;
                }

                if (oldExpectedLength > 0
                    && previous.Length != oldExpectedLength)
                {
                    Logging.Error(
                        "Compatibility array "
                        + label
                        + " has an unexpected length "
                        + previous.Length
                        + "; expected "
                        + oldExpectedLength
                        + " or "
                        + newLength
                        + ".");
                    return;
                }

                Array replacement =
                    Array.CreateInstance(elementType, newLength);
                Array.Copy(previous, replacement, previous.Length);
                InitializeRange(
                    replacement,
                    oldExpectedLength,
                    newLength,
                    factory);
                field.SetValue(target, replacement);
                Logging.Info(
                    "Expanded compatibility array "
                    + label
                    + ": "
                    + previous.Length
                    + " -> "
                    + newLength
                    + ".");
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to expand compatibility array "
                    + label
                    + ": "
                    + exception);
            }
        }

        internal static void ExpandSplit(
            FieldInfo field,
            object target,
            int oldItemCapacity,
            int newItemCapacity,
            SplitElementFactory factory,
            string label)
        {
            if (field == null)
            {
                Logging.Warning(
                    "Could not find split compatibility array "
                    + label
                    + ".");
                return;
            }

            try
            {
                Type elementType = field.FieldType.GetElementType();
                if (elementType == null)
                {
                    Logging.Warning(
                        "Compatibility member is not an array: "
                        + label
                        + ".");
                    return;
                }

                int oldLength = oldItemCapacity * 2;
                int newLength = newItemCapacity * 2;
                Array previous = field.GetValue(target) as Array;
                if (previous == null)
                {
                    Logging.Info(
                        "Split compatibility array is not initialized yet; "
                        + "skipping "
                        + label
                        + ".");
                    return;
                }

                if (previous.Length == newLength)
                {
                    return;
                }

                if (previous.Length != oldLength)
                {
                    Logging.Error(
                        "Split compatibility array "
                        + label
                        + " has an unexpected length "
                        + previous.Length
                        + "; expected "
                        + oldLength
                        + " or "
                        + newLength
                        + ".");
                    return;
                }

                Array replacement =
                    Array.CreateInstance(elementType, newLength);
                Array.Copy(
                    previous,
                    0,
                    replacement,
                    0,
                    oldItemCapacity);
                Array.Copy(
                    previous,
                    oldItemCapacity,
                    replacement,
                    newItemCapacity,
                    oldItemCapacity);

                for (int i = oldItemCapacity;
                    i < newItemCapacity;
                    ++i)
                {
                    replacement.SetValue(
                        CreateSplitElement(factory, i, true),
                        i);
                    replacement.SetValue(
                        CreateSplitElement(factory, i, false),
                        newItemCapacity + i);
                }

                field.SetValue(target, replacement);
                Logging.Info(
                    "Migrated and expanded split compatibility array "
                    + label
                    + ": "
                    + previous.Length
                    + " -> "
                    + newLength
                    + " (offset "
                    + oldItemCapacity
                    + " -> "
                    + newItemCapacity
                    + ").");
            }
            catch (Exception exception)
            {
                Logging.Error(
                    "Failed to migrate split compatibility array "
                    + label
                    + ": "
                    + exception);
            }
        }

        private static void InitializeRange(
            Array array,
            int start,
            int end,
            IndexedElementFactory factory)
        {
            if (factory == null)
            {
                return;
            }

            for (int i = start; i < end; ++i)
            {
                array.SetValue(factory(i), i);
            }
        }

        private static object CreateSplitElement(
            SplitElementFactory factory,
            int index,
            bool startNode)
        {
            return factory == null
                ? null
                : factory(index, startNode);
        }
    }
}
