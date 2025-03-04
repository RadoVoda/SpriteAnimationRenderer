using UnityEngine;
using UnityEditor;

namespace DOTSSpriteAnimation
{
    public class JustReadAttribute : PropertyAttribute
    {
        public JustReadAttribute() { }
    }

#if UNITY_EDITOR
    /// <summary>
    /// A custom property drawer for JustRead attribute
    /// </summary>
    /// <seealso cref="UnityEditor.PropertyDrawer" />
    [CustomPropertyDrawer(typeof(JustReadAttribute))]
    public class JustReadDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUI.GetPropertyHeight(property, label, true);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            GUI.enabled = false;
            EditorGUI.PropertyField(position, property, label, true);
            GUI.enabled = true;
        }
    }
#endif
}