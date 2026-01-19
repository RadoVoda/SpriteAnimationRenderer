using UnityEngine;
using System.Collections.Generic;
using UnityEditor;
using Unity.Mathematics;

namespace DOTSSpriteAnimation
{
    public enum ESpritePlayMode
    {
        Once,//Animation play once from start to the end and then stops at last frame
        Loop,//Animation plays in a loop from start to the end in cycles
        Forward,//Animation plays forward from start to the end and then automatically switch to Reverse mode
        Reverse//Animation plays backward from end to the start and then automatically switch to Forward mode
    }

    [CreateAssetMenu(fileName = "SpriteAnimation", menuName = "SpriteAnimationRenderer/SpriteAnimation", order = 0)]
    [System.Serializable]
    public class SpriteAnimationTemplate : ScriptableObject
    {
        [Tooltip("Unique animation template ID used to identify animation in SpriteAnimationRenderer")]
        public SerializableGuid guid = SerializableGuid.NewGuid();

        [Tooltip("Duration of one full animation cycle in seconds")]
        [JustRead]
        public float playTime;

        [Tooltip("Default animation frame duration in seconds")]
        public float frameTime = 1f;

        [Tooltip("Sprite index where animation starts")]
        public int startIndex = 0;

        [Tooltip("Controls if and how to repeat the animation")]
        public ESpritePlayMode repetition = ESpritePlayMode.Loop;

        [Tooltip("Optional Animation Clip reference used to load animation data")]
        public AnimationClip clip;

        [Tooltip("Optional Texture to load sprite data")]
        public Texture2D texture;

        [Tooltip("Each animation keyframe is a pair of sprite and associated play time")]
        public List<SpriteKeyframe> frames = new();

        [HideInInspector]
        public List<TransformKeyframe> transforms = new();

        [HideInInspector]
        public List<ColorKeyframe> colors = new();

        [HideInInspector]
        public Sprite[] sprites;

        public HashGuid GetGuid() => guid;

        public ref readonly SpriteAnimationData GetData() => ref data;

        [SerializeField, HideInInspector]
        private SpriteAnimationData data;

        void Awake()
        {
            if (guid.IsEmpty)
            {
                guid = SerializableGuid.NewGuid();
            }
        }

#if UNITY_EDITOR

        void OnValidate()
        {
            playTime = 0f;

            if (clip == null && frames.Count == 0 && sprites.Length > 0)
            {
                foreach (var sprite in sprites)
                {
                    frames.Add(new SpriteKeyframe { sprite = sprite, time = frameTime });
                }
            }

            ReloadTexture();
            UpdatePlayTime();
            data = new SpriteAnimationData(this);
            EditorUtility.SetDirty(this);
        }

        public void ReloadAnimationClip()
        {
            if (clip != null)
            {
                SpriteAnimationRenderer.GetSpriteKeyframesFromAnimationClip(clip, frames);
                SpriteAnimationRenderer.GetTransformsFromAnimationClip(clip, transforms);
                SpriteAnimationRenderer.GetColorsFromAnimationClip(clip, colors);
            }

            ReloadTexture();
            UpdatePlayTime();
        }

        private void UpdatePlayTime()
        {
            if (frames.Count > 0)
            {
                playTime = 0f;

                for (int i = 0; i < frames.Count; ++i)
                {
                    playTime += frames[i].time;
                }

                if (repetition == ESpritePlayMode.Forward || repetition == ESpritePlayMode.Reverse)
                {
                    playTime *= 2;
                    playTime -= frames[frames.Count - 1].time;
                }
            }
        }

        private void ReloadTexture()
        {
            if (texture != null)
            {
                List<Sprite> sprites = new List<Sprite>();
                SpriteAnimationRenderer.GetSpritesFromTexture2D(texture, sprites);

                if (frames.Count > sprites.Count)
                {
                    frames.RemoveRange(sprites.Count, frames.Count - sprites.Count);
                }

                for (int i = 0; i < sprites.Count; ++i)
                {
                    if (i < frames.Count)
                    {
                        var frame = frames[i];
                        frame.sprite = sprites[i];
                        frames[i] = frame;
                    }
                    else
                    {
                        frames.Add(new SpriteKeyframe { sprite = sprites[i], time = frameTime });
                    }
                }
            }
        }
#endif
    }

    [System.Serializable]
    public struct SpriteKeyframe
    {
        public Sprite sprite;
        public float time;
    }

    [System.Serializable]
    public struct TransformKeyframe
    {
        public float4x2 transform;
        public float time;
    }

    [System.Serializable]
    public struct ColorKeyframe
    {
        public float4 color;
        public float time;
    }

#if UNITY_EDITOR

    // Editor window for quick analysis of animation clip data
    public class ClipInfo : EditorWindow
    {
        private AnimationClip clip;

        [MenuItem("Window/Clip Info")]
        static void Init()
        {
            GetWindow(typeof(ClipInfo));
        }

        public void OnGUI()
        {
            clip = EditorGUILayout.ObjectField("Clip", clip, typeof(AnimationClip), false) as AnimationClip;

            if (clip != null)
            {
                EditorGUILayout.LabelField("Length: " + clip.length);

                EditorGUILayout.LabelField("Curves:");

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                    EditorGUILayout.LabelField(binding.path + "/" + binding.propertyName + "/" + binding.type + "/ Keys: " + curve.keys.Length);
                }

                List<SpriteKeyframe> frames = new();
                List<TransformKeyframe> transforms = new();
                List<ColorKeyframe> colors = new();

                SpriteAnimationRenderer.GetSpriteKeyframesFromAnimationClip(clip, frames);
                SpriteAnimationRenderer.GetTransformsFromAnimationClip(clip, transforms);
                SpriteAnimationRenderer.GetColorsFromAnimationClip(clip, colors);

                EditorGUILayout.LabelField("Frames:");

                foreach (var frame in frames)
                {
                    EditorGUILayout.LabelField("Frame sprite: " + frame.sprite.name + " Frame time: " + frame.time);
                }

                EditorGUILayout.LabelField("Transforms:");

                foreach (var frame in transforms)
                {
                    EditorGUILayout.LabelField("Frame transform: " + frame.transform + " Frame time: " + frame.time);
                }

                EditorGUILayout.LabelField("Colors:");

                foreach (var frame in colors)
                {
                    EditorGUILayout.LabelField("Frame color: " + frame.color + " Frame time: " + frame.time);
                }
            }
        }
    }

    [CustomPropertyDrawer(typeof(SpriteKeyframe))]
    public class SpriteKeyframeDrawer : PropertyDrawer
    {
        // Draw the property inside the given rect
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Using BeginProperty / EndProperty on the parent property means that prefab override logic works on the entire property
            EditorGUI.BeginProperty(position, label, property);

            // Draw label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
            float panelSize = position.width / 4;

            // Don't make child fields be indented
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            // Calculate rects
            Rect spriteRect = new Rect(position.xMin, position.yMin, panelSize * 3, position.height);
            Rect timeRect = new Rect(position.xMin + panelSize * 3 + 5, position.yMin, panelSize, position.height);

            // Draw fields - pass GUIContent.none to each so they are drawn without labels
            EditorGUI.PropertyField(spriteRect, property.FindPropertyRelative("sprite"), GUIContent.none);
            EditorGUI.PropertyField(timeRect, property.FindPropertyRelative("time"), GUIContent.none);

            EditorGUI.LabelField(spriteRect, new GUIContent("", "Animation sprite frame"));
            EditorGUI.LabelField(timeRect, new GUIContent("", "Duration of this frame in seconds"));

            // Set indent back to what it was
            EditorGUI.indentLevel = indent;

            EditorGUI.EndProperty();
        }
    }

    [CustomPropertyDrawer(typeof(TransformKeyframe))]
    public class TransformKeyframeDrawer : PropertyDrawer
    {
        // Draw the property inside the given rect
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Using BeginProperty / EndProperty on the parent property means that prefab override logic works on the entire property
            EditorGUI.BeginProperty(position, label, property);

            // Draw label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
            float panelSize = position.width / 4;

            // Don't make child fields be indented
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            // Calculate rects
            Rect transformRect = new Rect(position.xMin, position.yMin, panelSize * 3, position.height);
            Rect timeRect = new Rect(position.xMin + panelSize * 3 + 5, position.yMin, panelSize, position.height);

            // Draw fields - pass GUIContent.none to each so they are drawn without labels
            EditorGUI.PropertyField(transformRect, property.FindPropertyRelative("transform"), GUIContent.none);
            EditorGUI.PropertyField(timeRect, property.FindPropertyRelative("time"), GUIContent.none);

            EditorGUI.LabelField(transformRect, new GUIContent("", "Animation transform"));
            EditorGUI.LabelField(timeRect, new GUIContent("", "Duration of this frame in seconds"));

            // Set indent back to what it was
            EditorGUI.indentLevel = indent;

            EditorGUI.EndProperty();
        }
    }

    [CustomPropertyDrawer(typeof(ColorKeyframe))]
    public class ColorKeyframeDrawer : PropertyDrawer
    {
        // Draw the property inside the given rect
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // Using BeginProperty / EndProperty on the parent property means that prefab override logic works on the entire property
            EditorGUI.BeginProperty(position, label, property);

            // Draw label
            position = EditorGUI.PrefixLabel(position, GUIUtility.GetControlID(FocusType.Passive), label);
            float panelSize = position.width / 4;

            // Don't make child fields be indented
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            // Calculate rects
            Rect colorRect = new Rect(position.xMin, position.yMin, panelSize * 3, position.height);
            Rect timeRect = new Rect(position.xMin + panelSize * 3 + 5, position.yMin, panelSize, position.height);

            // Draw fields - pass GUIContent.none to each so they are drawn without labels
            EditorGUI.PropertyField(colorRect, property.FindPropertyRelative("color"), GUIContent.none);
            EditorGUI.PropertyField(timeRect, property.FindPropertyRelative("time"), GUIContent.none);

            EditorGUI.LabelField(colorRect, new GUIContent("", "Animation transform"));
            EditorGUI.LabelField(timeRect, new GUIContent("", "Duration of this frame in seconds"));

            // Set indent back to what it was
            EditorGUI.indentLevel = indent;

            EditorGUI.EndProperty();
        }
    }

    [CustomEditor(typeof(SpriteAnimationTemplate))]
    public class SpriteAnimationEditor : Editor
    {
        private static string buttonText = "Reload Animation Clip";
        private static string buttonTooltip = "Reloads frame data from associated valid AnimationClip";

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            var script = (SpriteAnimationTemplate)target;

            if (script != null && GUILayout.Button(new GUIContent(buttonText, buttonTooltip), GUILayout.Height(20), GUILayout.Width(200)))
            {
                script.ReloadAnimationClip();
            }
        }
    }

#endif
}

