using System;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using BurstEnums;

namespace DOTSSpriteAnimation
{
    public struct SpriteTransform : IComponentData
    {
        public float3 position;
        public float scale;
        public quaternion rotation;

        public float4x2 value => new float4x2(new float4(position, scale), rotation.value);

        public SpriteTransform(float3 Position, float3 Rotation, float Scale = 1f)
        {
            position = Position;
            scale = Scale;
            rotation = Rotation.Equals(float3.zero) ? quaternion.identity : quaternion.EulerXYZ(math.radians(Rotation)).value;
        }

        public static implicit operator float4x2(SpriteTransform transform) => new float4x2(new float4(transform.position, transform.scale), transform.rotation.value);
        public static implicit operator SpriteTransform(float4x2 transform) => new SpriteTransform { position = transform[0].xyz, scale = transform[0].w, rotation = new quaternion(transform[1]) };
    }

    public struct SpriteMovement : IComponentData
    {
        public enum ESpriteTrajectory
        {
            [Tooltip("No movement is applied at all")]
            None,
            [Tooltip("No movement, change from origin to targer upon completion")]
            Port,
            [Tooltip("Move in straight line from origin to target")]
            Line,
            [Tooltip("Move in ballistic curve from oigin to target")]
            Curve
        }

        public float3 target;
        public float3 origin;
        public float progress;
        public float duration;
        public ESpriteTrajectory trajectory;

        public SpriteMovement(float3 Target, float Duration = 0f, ESpriteTrajectory Trajectory = ESpriteTrajectory.Line)
        {
            target = Target;
            origin = float3.zero;
            progress = 0;
            duration = Duration;
            trajectory = Trajectory;
        }
    }

    public struct SpriteRotation : IComponentData
    {
        public enum ESpriteRotation
        {
            [Tooltip("No rotation is applied")]
            None,
            [Tooltip("Add this rotation to the existing rotation")]
            Relative,
            [Tooltip("Set existing rotation to this value")]
            Absolute,
            [Tooltip("Continuously add this rotation to the existing rotation over time")]
            Spin
        }
        
        public float3 target;
        public float3 origin;
        public float progress;
        public float duration;
        public ESpriteRotation policy;

        public SpriteRotation(float3 Rotation, float Duration = 0f, ESpriteRotation Policy = ESpriteRotation.Relative)
        {
            target = Rotation;
            origin = float3.zero;
            progress = 0;
            duration = Duration;
            policy = Policy;

            if (policy == ESpriteRotation.Spin && duration <= 0f)
            {
                duration = 1f;
            }
        }
    }

    public struct SpriteEulerAngle : IComponentData
    {
        public float3 value;
    }

    public struct SpriteIndex : IComponentData
    {
        public int value;

        public SpriteIndex(int Value)
        {
            value = Value;
        }
    }

    public struct SpriteBuffer : IComponentData
    {
        public Entity bufferEntity;

        public SpriteBuffer(Entity BufferEntity)
        {
            bufferEntity = BufferEntity;
        }
    }

    public struct SpriteAnimation : IComponentData
    {
        public HashGuid guid;

        public SpriteAnimation(HashGuid guid)
        {
            this.guid = guid;
        }
    }

    public struct SpritePlaySpeed : IComponentData
    {
        public float value;

        public SpritePlaySpeed(float speed)
        {
            value = speed;
        }
    }

    public struct SpriteLifetime : IComponentData
    {
        public Entity entity;
        public float time;

        public SpriteLifetime(Entity e, float lifetime = 0f)
        {
            entity = e;
            time = lifetime;
        }
    }

    public struct SpriteState : IComponentData
    {
        [Flags]
        public enum ESpriteState : ushort
        {
            [Tooltip("Play animation, i.e cycle frames over time")]
            Play = 1 << 0,
            [Tooltip("Render animation instance")]
            Show = 1 << 1,
            [Tooltip("Check lifetime")]
            Life = 1 << 2,
        }

        public static readonly SpriteState Default = new SpriteState(EnumBase<ESpriteState>.All);
        public ESpriteState value;

        public SpriteState(ESpriteState state)
        {
            value = state;
        }
    }

    public struct SpriteColor : IComponentData
    {
        public float4 value;

        public SpriteColor(Color color)
        {
            value.x = color.r;
            value.y = color.g;
            value.z = color.b;
            value.w = color.a;
        }

        public static readonly SpriteColor Default = new SpriteColor(UnityEngine.Color.white);

        public static implicit operator Color(SpriteColor shc) => new Color(shc.value.x, shc.value.y, shc.value.z, shc.value.w);
        public static implicit operator SpriteColor(Color col) => new SpriteColor(col);
    }

    public struct SpritePaint : IComponentData
    {
        public uint2 value;

        public SpritePaint(uint2 index)
        {
            value = index;
        }
    }

    public struct SpriteBufferData : IComponentData
    {
        public int length;
    }

    public readonly struct SpriteAnimationData : IComponentData
    {
        public readonly ESpritePlayMode mode;
        public readonly int length;
        public readonly int start;
        public readonly float play;
        public readonly HashGuid guid;

        public SpriteAnimationData(SpriteAnimationTemplate animation)
        {
#if UNITY_EDITOR
            animation.ReloadAnimationClip();
#endif
            mode = animation.repetition;
            length = animation.frames.Count;
            start = animation.startIndex;
            play = animation.playTime;
            guid = animation.guid.GetGuid();
        }

        public HashGuid GetGuid() => guid;
    }

    public struct SpritePlayback : IComponentData
    {
        public float time;
        public float life;
        public int length;
        public ESpritePlayMode mode;

        public SpritePlayback(float time, float life, int length, ESpritePlayMode mode)
        {
            this.time = time;
            this.life = life;
            this.length = length;
            this.mode = mode;
        }
    }

    public struct SpriteKeyframeClocks : IComponentData
    {
        public float transformKeyframeTime;
        public float colorKeyframeTime;
        public int transformKeyframeIndex;
        public int colorKeyframeIndex;

        public SpriteKeyframeClocks(SpriteKeyframeData keyframeData)
        {
            transformKeyframeTime = 0f;
            colorKeyframeTime = 0f;
            transformKeyframeIndex = keyframeData.data.transformFrames.Length > 0 ? 0 : -1;
            colorKeyframeIndex = keyframeData.data.colorFrames.Length > 0 ? 0 : -1;
        }
    }

    [InternalBufferCapacity(0)]
    public struct SpriteIndexBuffer : IBufferElementData
    {
        public int value;

        public SpriteIndexBuffer(int index)
        {
            value = index;
        }

        public static implicit operator int(SpriteIndexBuffer e) => e.value;
        public static implicit operator SpriteIndexBuffer(int e) => new SpriteIndexBuffer { value = e };
    }

    [InternalBufferCapacity(0)]
    public struct SpriteTransformBuffer : IBufferElementData
    {
        public float4x2 matrix;

        public SpriteTransformBuffer(float4x2 matrix)
        {
            this.matrix = matrix;
        }

        public static implicit operator float4x2(SpriteTransformBuffer buffer) => buffer.matrix;
        public static implicit operator SpriteTransformBuffer(float4x2 buffer) => new SpriteTransformBuffer { matrix = buffer };
    }

    [InternalBufferCapacity(0)]
    public struct SpriteColorBuffer : IBufferElementData
    {
        public uint3 value;

        public SpriteColorBuffer(float4 color, uint2 paint)
        {
            value.x = SpriteAnimationRenderer.Encode(color);
            value.y = paint.x;
            value.z = paint.y;
        }

        public static implicit operator uint3(SpriteColorBuffer buffer) => buffer.value;
        public static implicit operator SpriteColorBuffer(uint3 buffer) => new SpriteColorBuffer { value = buffer };
    }

    [InternalBufferCapacity(0)]
    public struct SpriteUvBuffer : IBufferElementData
    {
        public float4 uv;

        public SpriteUvBuffer(float4 uv)
        {
            this.uv = uv;
        }

        public static implicit operator float4(SpriteUvBuffer buffer) => buffer.uv;
        public static implicit operator SpriteUvBuffer(float4 buffer) => new SpriteUvBuffer { uv = buffer };
    }

    [InternalBufferCapacity(0)]
    public struct SpriteEntityBuffer : IBufferElementData
    {
        public Entity entity;

        public SpriteEntityBuffer(Entity entity)
        {
            this.entity = entity;
        }

        public static implicit operator Entity(SpriteEntityBuffer buffer) => buffer.entity;
        public static implicit operator SpriteEntityBuffer(Entity e) => new SpriteEntityBuffer { entity = e };
    }

    public struct SpriteKeyframeBlob
    {
        public BlobArray<float4x2> transforms;
        public BlobArray<float4> colors;
        public BlobArray<float> spriteFrames;
        public BlobArray<float> transformFrames;
        public BlobArray<float> colorFrames;
    }

    public struct SpriteKeyframeData : IComponentData
    {
        private BlobAssetReference<SpriteKeyframeBlob> blob;

        public SpriteKeyframeData(BlobAssetReference<SpriteKeyframeBlob> blob)
        {
            this.blob = blob;
        }

        public bool IsValid => blob.IsCreated;
        public ref SpriteKeyframeBlob data => ref blob.Value;
    }
}