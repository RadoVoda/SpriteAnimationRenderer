using System;
using UnityEngine;
using UnityEditor;
using Unity.Collections.LowLevel.Unsafe;

namespace DOTSSpriteAnimation
{
    public interface IHashGuid
    {
        public HashGuid GetGuid();
    }

    /// <summary>
    /// Serializable wrapper class for System.Guid.
    /// Can be implicitly converted to/from System.Guid.
    /// Guid is not blittable and thus cannot be directly used by Burst compiler.
    /// </summary>
    [Serializable]
    public struct SerializableGuid : ISerializationCallbackReceiver, IEquatable<SerializableGuid>, IFormattable, IHashGuid
    {
        private Guid value;

        [JustRead]
        [SerializeField]
        private string name;

        public static readonly SerializableGuid Empty = Guid.Empty;

        public bool IsEmpty => value == Guid.Empty;

        public SerializableGuid(Guid guid)
        {
            value = guid;
            name = guid.ToString();
        }

        public HashGuid GetGuid() => new HashGuid(value);

        public Guid ToGuid() => value;

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public void OnAfterDeserialize()
        {
            try
            {
                value = Guid.Parse(name);
            }
            catch
            {
                value = Guid.Empty;
                Debug.LogWarning($"Attempted to parse invalid GUID string '{name}'. GUID will set to System.Guid.Empty");
            }
        }

        public void OnBeforeSerialize()
        {
            name = value.ToString();
        }

        public static SerializableGuid NewGuid() => new SerializableGuid(Guid.NewGuid());

        public override string ToString() => value.ToString();

        public string ToString(string format, IFormatProvider formatProvider) => value.ToString(format, formatProvider);

        public override bool Equals(object other) => other is SerializableGuid guid && Equals(guid);
        public bool Equals(SerializableGuid other) => other.value == value;

        public static bool operator ==(SerializableGuid a, SerializableGuid b) => a.value == b.value;
        public static bool operator !=(SerializableGuid a, SerializableGuid b) => a.value != b.value;

        public static implicit operator SerializableGuid(Guid guid) => new SerializableGuid(guid);
        public static implicit operator Guid(SerializableGuid serializable) => serializable.value;

        public static implicit operator SerializableGuid(string serializedGuid) => new SerializableGuid(Guid.Parse(serializedGuid));
        public static implicit operator string(SerializableGuid serializedGuid) => serializedGuid.ToString();
    }

    /// <summary>
    /// Wrapper to store System.Guid as blittable struct Hash128 value
    /// Can be implicitly converted to/from System.Guid and SerializableGuid.
    /// </summary>
    public readonly struct HashGuid : IEquatable<HashGuid>, IEquatable<SerializableGuid>, IComparable<HashGuid>, IFormattable
    {
        public readonly Unity.Entities.Hash128 value;

        public static readonly HashGuid Empty;

        public bool IsEmpty => !value.IsValid;

        public HashGuid(Unity.Entities.Hash128 hash)
        {
            value = hash;
        }

        public HashGuid(Guid guid)
        {
            value = UnsafeUtility.As<Guid, Hash128>(ref guid);
        }

        public unsafe ref Guid ToGuid()
        {
            Hash128* ptr = stackalloc Hash128[1];
            *ptr = value;
            return ref UnsafeUtility.AsRef<Guid>(ptr);
        }

        public static HashGuid NewGuid() => new HashGuid(Guid.NewGuid());

        public override string ToString() => value.ToString();

        public string ToString(string format, IFormatProvider formatProvider) => ToGuid().ToString(format, formatProvider);

        public override int GetHashCode()
        {
            return value.GetHashCode();
        }

        public int CompareTo(HashGuid other) => value.CompareTo(other.value);
        public override bool Equals(object other) => other is HashGuid blittable && Equals(blittable);
        public bool Equals(HashGuid other) => other.value == value;
        public bool Equals(SerializableGuid other) => other.ToGuid() == ToGuid();
        public static bool operator ==(HashGuid a, HashGuid b) => a.value == b.value;
        public static bool operator !=(HashGuid a, HashGuid b) => !(a == b);
        public static bool operator <(HashGuid a, HashGuid b) => a.value < b.value;
        public static bool operator >(HashGuid a, HashGuid b) => a.value > b.value;

        public static implicit operator HashGuid(SerializableGuid entry) => new HashGuid(entry.ToGuid());
        public static implicit operator SerializableGuid(HashGuid entry) => new SerializableGuid(entry.ToGuid());

        public static implicit operator Guid(HashGuid entry) => entry.ToGuid();
        public static implicit operator HashGuid(Guid entry) => new HashGuid(entry);

        public static implicit operator Unity.Entities.Hash128(HashGuid entry) => entry.value;
        public static implicit operator HashGuid(Unity.Entities.Hash128 entry) => new HashGuid(entry);

        public static implicit operator UnityEngine.Hash128(HashGuid entry) => entry.value;
        public static implicit operator HashGuid(UnityEngine.Hash128 entry) => new HashGuid(entry);
    }

#if UNITY_EDITOR

    /// <summary>
    /// Property drawer for SerializableGuid
    /// </summary>
    [CustomPropertyDrawer(typeof(SerializableGuid))]
    public class SerializableGuidPropertyDrawer : PropertyDrawer
    {
        const string lStyle = "miniButtonLeft";
        const string mStyle = "miniButtonMid";
        const string rStyle = "miniButtonRight";
        private const float buttonWidth = 30f;
        private const int buttonCount = 3;
        private static readonly GUIContent newContent = EditorGUIUtility.IconContent("d_refresh");
        private static readonly GUIContent copyContent = EditorGUIUtility.IconContent("SaveAs");
        private static readonly GUIContent emptyContent = EditorGUIUtility.IconContent("Grid.EraserTool");

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Get property
            SerializedProperty serializedGuid = property.FindPropertyRelative("name");
            newContent.tooltip = "New";
            copyContent.tooltip = "Copy";
            emptyContent.tooltip = "Empty";

            // Draw label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);

            Rect buttonRect = position;
            buttonRect.xMin = position.xMax;
            buttonRect.x -= buttonWidth;
            buttonRect.width = buttonWidth;

            // Buttons
            if (GUI.Button(buttonRect, emptyContent, rStyle))
            {
                serializedGuid.stringValue = Guid.Empty.ToString();
            }

            buttonRect.x -= buttonWidth;

            if (GUI.Button(buttonRect, copyContent, mStyle))
            {
                EditorGUIUtility.systemCopyBuffer = serializedGuid.stringValue;
            }

            buttonRect.x -= buttonWidth;

            if (GUI.Button(buttonRect, newContent, lStyle))
            {
                serializedGuid.stringValue = Guid.NewGuid().ToString();
            }

            // Draw fields - pass GUIContent.none to each so they are drawn without labels
            Rect guidRect = position;
            guidRect.width -= buttonWidth * buttonCount;
            EditorGUI.PropertyField(guidRect, serializedGuid, GUIContent.none);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }

#endif
}