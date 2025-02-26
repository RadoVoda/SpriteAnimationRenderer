
using UnityEditor;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Unity.Jobs;
using Unity.Collections;
using Unity.Burst;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst.Intrinsics;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System;
using BurstEnums;

namespace SpriteAnimation
{
    public partial class SpriteAnimationRenderer : SystemBase
    {
        private Mesh mesh = Quad();
        private Bounds bounds = new Bounds(Vector3.zero, new Vector3(100000000.0f, 100000000.0f, 1.0f));
        private EndSimulationEntityCommandBufferSystem bufferSystem;
        private EntityArchetype bufferArchetype;
        private NativeParallelHashMap<HashGuid, Entity> animationBufferEntityMap;
        private Dictionary<Entity, SpriteAnimationRenderPayload> bufferPayloadMap = new();
        private NativeList<float4x3> paints;
        private const float bufferLengthToCapacityThreshold = 0.8f;
        private const int bufferLengthMinimumCapacity = 256;
        private float4x4 nextCullingFrustrum, lastCullingFrustrum;
        private EntityQuery indexQuery, colorQuery, paintQuery, transformQuery;
        private EntityTypeHandle entityTypeHandle;
        private ComponentTypeHandle<SpriteBuffer> spriteBufferTypeHandle;
        private ComponentTypeHandle<SpriteIndex> spriteIndexTypeHandle;
        private ComponentTypeHandle<SpriteColor> spriteColorTypeHandle;
        private ComponentTypeHandle<SpritePaint> spritePaintTypeHandle;
        private ComponentTypeHandle<SpriteTransform> spriteTransformTypeHandle;
        private const string uvBufferName = "uvBuffer";
        private const string indexBufferName = "indexBuffer";
        private const string colorBufferName = "colorBuffer";
        private const string transformBufferName = "transformBuffer";
        private const string paintBufferName = "paintBuffer";

        public Camera frustrumCullingCamera = null;
        public bool cullingEnabled { get; private set; }
        public EntityArchetype spriteArchetype { get; private set; }

        public static SpriteAnimationRenderer instance => Instance();

        public static SpriteAnimationRenderer Instance(World world = null)
        {
            if (world == null)
                world = World.DefaultGameObjectInjectionWorld;

            return world != null ? world.GetOrCreateSystemManaged<SpriteAnimationRenderer>() : null;
        }

        public static bool TryGetInstance(out SpriteAnimationRenderer instance, World world = null)
        {
            instance = Instance(world);
            return instance != null;
        }

        public NativeArray<float4x3>.ReadOnly GetPaints() => paints.AsReadOnly();

        public struct InstantiateSpriteData
        {
            public SpriteAnimation animation;
            public SpriteTransform transform;
            public SpriteMovement movement;
            public SpriteRotation rotation;
            public SpriteColor color;
            public SpriteState state;
            public SpritePlaySpeed speed;
            public SpriteLifetime parent;

            public static InstantiateSpriteData MakeNew() => new InstantiateSpriteData()
            {
                color = SpriteColor.Default,
                state = SpriteState.Default,
                speed = new SpritePlaySpeed(1f)
            };
        }

        [Serializable]
        public struct PaintConfig : IEquatable<PaintConfig>
        {
            [Tooltip("Target color to match and apply paint to. Only RGB channels are used for color matching, but Paint Alpha will be applied as well.")]
            public UnityEngine.Color color;
            [Tooltip("Paint to be applied to target color. Paint will modify color Hue, while Saturation and Value (Brightness) of original color remains.")]
            public UnityEngine.Color paint;
            [Tooltip("Tolerance for RGB color exact match, greater the value the more different colors will be fully painted on.")]
            [Range(0, 1)]
            public float threshold;
            [Tooltip("Tolerance for RGB color partial match, greater the value the more different colors will be somewhat painted on, depending on the actual color difference on the RGB scale.")]
            [Range(0, 1)]
            public float smooth;
            [Tooltip("Controls final blend ratio between original color and the new paint & color mix. This also controls final Alpha blend.")]
            [Range(0, 1)]
            public float blend;
            [Tooltip("Any pixel with final color (after paint) with Alpha below this value will be clipped off and not rendered.")]
            [Range(0, 1)]
            public float cutoff;

            public override int GetHashCode() => ((float4x3)this).GetHashCode();
            public override bool Equals(object other) => other is PaintConfig key && Equals(key);
            public bool Equals(PaintConfig other) => ((float4x3)this).Equals((float4x3)other);
            public static bool operator ==(PaintConfig a, PaintConfig b) => a.Equals(b);
            public static bool operator !=(PaintConfig a, PaintConfig b) => !(a == b);

            public static implicit operator float4x3(PaintConfig paintConfig)
            {
                float4 color = new float4(paintConfig.color.r, paintConfig.color.g, paintConfig.color.b, paintConfig.color.a);
                float4 paint = new float4(paintConfig.paint.r, paintConfig.paint.g, paintConfig.paint.b, paintConfig.paint.a);
                float4 setup = new float4(paintConfig.threshold, paintConfig.smooth, paintConfig.blend, paintConfig.cutoff);
                return new float4x3(color, paint, setup);
            }

            public static implicit operator PaintConfig(float4x3 data)
            {
                return new PaintConfig
                {
                    color = new UnityEngine.Color(data.c0.x, data.c0.y, data.c0.z, data.c0.w),
                    paint = new UnityEngine.Color(data.c1.x, data.c1.y, data.c1.z, data.c1.w),
                    threshold = data.c2.x,
                    smooth = data.c2.y,
                    blend = data.c2.z,
                    cutoff = data.c2.w
                };
            }
        }

        protected override void OnCreate()
        {
            spriteArchetype = EntityManager.CreateArchetype
            (
                typeof(SpriteTransform),
                typeof(SpriteMovement),
                typeof(SpriteRotation),
                typeof(SpriteEulerAngle),
                typeof(SpriteIndex),
                typeof(SpriteColor),
                typeof(SpritePaint),
                typeof(SpriteBuffer),
                typeof(SpritePlayback),
                typeof(SpriteAnimation),
                typeof(SpriteState),
                typeof(SpriteLifetime),
                typeof(SpritePlaySpeed),
                typeof(SpriteKeyframeData),
                typeof(SpriteKeyframeClocks)
            );

            bufferArchetype = EntityManager.CreateArchetype
            (
                typeof(SpriteEntityBuffer),
                typeof(SpriteIndexBuffer),
                typeof(SpriteTransformBuffer),
                typeof(SpriteColorBuffer),
                typeof(SpriteUvBuffer),
                typeof(SpriteAnimationData),
                typeof(SpriteBufferData),
                typeof(SpriteKeyframeData)
            );

            indexQuery = GetEntityQuery(ComponentType.ReadOnly<SpriteIndex>(), ComponentType.ReadOnly<SpriteBuffer>());
            indexQuery.SetChangedVersionFilter(ComponentType.ReadOnly<SpriteIndex>());
            colorQuery = GetEntityQuery(ComponentType.ReadOnly<SpriteColor>(), ComponentType.ReadOnly<SpriteBuffer>());
            colorQuery.SetChangedVersionFilter(ComponentType.ReadOnly<SpriteColor>());
            paintQuery = GetEntityQuery(ComponentType.ReadOnly<SpritePaint>(), ComponentType.ReadOnly<SpriteBuffer>());
            paintQuery.SetChangedVersionFilter(ComponentType.ReadOnly<SpritePaint>());
            transformQuery = GetEntityQuery(ComponentType.ReadOnly<SpriteTransform>(), ComponentType.ReadOnly<SpriteBuffer>());
            transformQuery.SetChangedVersionFilter(ComponentType.ReadOnly<SpriteTransform>());
            bufferSystem = World.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
            animationBufferEntityMap = new NativeParallelHashMap<HashGuid, Entity>(128, Allocator.Persistent);
            entityTypeHandle = GetEntityTypeHandle();
            spriteBufferTypeHandle = GetComponentTypeHandle<SpriteBuffer>(true);
            spriteIndexTypeHandle = GetComponentTypeHandle<SpriteIndex>(true);
            spriteColorTypeHandle = GetComponentTypeHandle<SpriteColor>(true);
            spritePaintTypeHandle = GetComponentTypeHandle<SpritePaint>(true);
            spriteTransformTypeHandle = GetComponentTypeHandle<SpriteTransform>(true);

            paints = new NativeList<float4x3>(128, Allocator.Persistent)
            {
                float4x3.zero
            };
        }

        protected override void OnDestroy()
        {
            CleanBuffers();

            if (animationBufferEntityMap.IsCreated)
                animationBufferEntityMap.Dispose();

            if (paints.IsCreated)
                paints.Dispose();
        }

        protected override void OnUpdate()
        {
            Entities
                .WithName("SpriteRendererDrawCall")
                .ForEach(
                (
                    Entity entity,
                    in DynamicBuffer<SpriteIndexBuffer> indexBuffer,
                    in DynamicBuffer<SpriteColorBuffer> colorBuffer,
                    in DynamicBuffer<SpriteTransformBuffer> transformBuffer,
                    in SpriteBufferData data
                ) =>
                {
                    if (bufferPayloadMap.TryGetValue(entity, out var payload))
                    {
                        if (data.length > 0)
                        {
                            payload.SetCount((uint)data.length);
                            payload.SetData(indexBufferName, indexBuffer.Reinterpret<int>().AsNativeArray(), data.length);
                            payload.SetData(colorBufferName, colorBuffer.Reinterpret<uint3>().AsNativeArray(), data.length);
                            payload.SetData(transformBufferName, transformBuffer.Reinterpret<float4x2>().AsNativeArray(), data.length);
                            payload.Draw(mesh, bounds);
                        }
                    }
                })
                .WithoutBurst()
                .Run();

            if (frustrumCullingCamera != null)
            {
                nextCullingFrustrum = GetCameraCullingFrustrum(frustrumCullingCamera);
                cullingEnabled = true;
            }
            else if (cullingEnabled)
            {
                nextCullingFrustrum = float4x4.zero;
                cullingEnabled = false;
            }

            var indexUpdateMap = new NativeParallelHashMap<Entity, SpriteIndex>(indexQuery.CalculateEntityCount(), Allocator.TempJob);
            var colorUpdateMap = new NativeParallelHashMap<Entity, SpriteColor>(colorQuery.CalculateEntityCount(), Allocator.TempJob);
            var paintUpdateMap = new NativeParallelHashMap<Entity, SpritePaint>(paintQuery.CalculateEntityCount(), Allocator.TempJob);
            var transformUpdateMap = new NativeParallelHashMap<Entity, SpriteTransform>(transformQuery.CalculateEntityCount(), Allocator.TempJob);
            var bufferUpdateMap = new NativeParallelHashMap<Entity, bool>(animationBufferEntityMap.Count(), Allocator.TempJob);

            var nextFrustrum = nextCullingFrustrum;
            var lastFrustrum = lastCullingFrustrum;
            var sameFrustrum = nextFrustrum.Equals(lastFrustrum);
            var cull = cullingEnabled;
            var animBufferMap = animationBufferEntityMap;
            var commandBuffer = bufferSystem.CreateCommandBuffer();
            var parallelBuffer = commandBuffer.AsParallelWriter();
            var storage = SystemAPI.GetEntityStorageInfoLookup();
            var sheets = SystemAPI.GetComponentLookup<SpriteAnimationData>(true);
            var frames = SystemAPI.GetComponentLookup<SpriteKeyframeData>(true);
            var buffers = SystemAPI.GetComponentLookup<SpriteBuffer>(true);
            var paintCount = (uint)paints.Length;
            
            entityTypeHandle.Update(this);
            spriteBufferTypeHandle.Update(this);
            spriteIndexTypeHandle.Update(this);
            spriteColorTypeHandle.Update(this);
            spritePaintTypeHandle.Update(this);
            spriteTransformTypeHandle.Update(this);

            var recordSpriteIndexChangeJob = new RecordSpriteDataChangeJob<SpriteIndex>()
            {
                entityTypeHandle = entityTypeHandle,
                componentTypeHandle = spriteIndexTypeHandle,
                bufferTypeHandle = spriteBufferTypeHandle,
                componentChanges = indexUpdateMap.AsParallelWriter(),
                bufferChanges = bufferUpdateMap.AsParallelWriter(),
                lastSystemVersion = LastSystemVersion
            };

            var recordSpriteColorChangeJob = new RecordSpriteDataChangeJob<SpriteColor>()
            {
                entityTypeHandle = entityTypeHandle,
                componentTypeHandle = spriteColorTypeHandle,
                bufferTypeHandle = spriteBufferTypeHandle,
                componentChanges = colorUpdateMap.AsParallelWriter(),
                bufferChanges = bufferUpdateMap.AsParallelWriter(),
                lastSystemVersion = LastSystemVersion
            };

            var recordSpritePaintChangeJob = new RecordSpriteDataChangeJob<SpritePaint>()
            {
                entityTypeHandle = entityTypeHandle,
                componentTypeHandle = spritePaintTypeHandle,
                bufferTypeHandle = spriteBufferTypeHandle,
                componentChanges = paintUpdateMap.AsParallelWriter(),
                bufferChanges = bufferUpdateMap.AsParallelWriter(),
                lastSystemVersion = LastSystemVersion
            };

            var recordSpriteMatrixChangeJob = new RecordSpriteDataChangeJob<SpriteTransform>()
            {
                entityTypeHandle = entityTypeHandle,
                componentTypeHandle = spriteTransformTypeHandle,
                bufferTypeHandle = spriteBufferTypeHandle,
                componentChanges = transformUpdateMap.AsParallelWriter(),
                bufferChanges = bufferUpdateMap.AsParallelWriter(),
                lastSystemVersion = LastSystemVersion
            };

            var indexChangeHandle = recordSpriteIndexChangeJob.ScheduleParallel(indexQuery, Dependency);
            var colorChangeHandle = recordSpriteColorChangeJob.ScheduleParallel(colorQuery, Dependency);
            var paintChangeHandle = recordSpritePaintChangeJob.ScheduleParallel(paintQuery, Dependency);
            var matrixChangeHandle = recordSpriteMatrixChangeJob.ScheduleParallel(transformQuery, Dependency);
            var indexAndMatrixHandle = JobHandle.CombineDependencies(Dependency, indexChangeHandle, matrixChangeHandle);
            var paintAndColorHandle = JobHandle.CombineDependencies(Dependency, paintChangeHandle, colorChangeHandle);
            Dependency = JobHandle.CombineDependencies(indexAndMatrixHandle, paintAndColorHandle);

            Entities
                .WithName("CheckSpriteLifetime")
                .WithReadOnly(storage)
                .ForEach(
                (
                    Entity entity,
                    int entityInQueryIndex,
                    in SpriteLifetime life
                ) =>
                {
                    if (life.entity != Entity.Null && storage.Exists(life.entity) == false)
                    {
                        parallelBuffer.DestroyEntity(entityInQueryIndex, entity);
                    }
                })
                .ScheduleParallel();

            Entities
                .WithName("UpdateSpriteEntities")
                .WithReadOnly(sheets)
                .WithReadOnly(frames)
                .WithReadOnly(animBufferMap)
                .WithReadOnly(storage)
                .WithChangeFilter<SpriteAnimation>()
                .ForEach(
                (
                    Entity entity,
                    int entityInQueryIndex,
                    in SpriteBuffer buffer,
                    in SpriteAnimation request,
                    in SpriteLifetime life,
                    in SpriteColor color,
                    in SpritePaint paint,
                    in SpriteTransform matrix
                ) =>
                {
                    if (life.entity != Entity.Null && storage.Exists(life.entity) == false)
                    {
                        return;
                    }

                    if (animBufferMap.TryGetValue(request.guid, out var bufferEntity) == false ||
                        (buffer.bufferEntity != Entity.Null && storage.Exists(buffer.bufferEntity) == false) ||
                        (buffer.bufferEntity != Entity.Null && sheets.HasComponent(buffer.bufferEntity) == false))
                    {
                        parallelBuffer.DestroyEntity(entityInQueryIndex, entity);
                        return;
                    }

                    if (bufferEntity != buffer.bufferEntity &&
                        sheets.TryGetComponent(bufferEntity, out var sheet) &&
                        frames.TryGetComponent(bufferEntity, out var frame))
                    {
                        float lifetime = life.time < 0f ? sheet.play : life.time;
                        uint2 paintIndex = paint.value;

                        if (paintIndex.x >= paintCount)
                            paintIndex.x = 0;

                        if (paintIndex.y >= paintCount)
                            paintIndex.y = 0;

                        parallelBuffer.SetComponent(entityInQueryIndex, entity, new SpriteBuffer(bufferEntity));
                        parallelBuffer.SetComponent(entityInQueryIndex, entity, new SpritePlayback(0, lifetime, sheet.length, sheet.mode));
                        parallelBuffer.SetComponent(entityInQueryIndex, entity, new SpriteIndex(sheet.start));
                        parallelBuffer.SetComponent(entityInQueryIndex, entity, new SpriteKeyframeClocks(frame));
                        parallelBuffer.SetComponent(entityInQueryIndex, entity, frame);

                        parallelBuffer.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteIndexBuffer(sheet.start));
                        parallelBuffer.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteColorBuffer(color.value, paintIndex));
                        parallelBuffer.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteTransformBuffer(matrix));
                        parallelBuffer.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteEntityBuffer(entity));
                    }
                })
                .ScheduleParallel();

            Entities
            .WithName("UpdateSpriteEntityBuffers")
            .WithReadOnly(buffers)
            .WithReadOnly(indexUpdateMap)
            .WithReadOnly(colorUpdateMap)
            .WithReadOnly(paintUpdateMap)
            .WithReadOnly(transformUpdateMap)
            .WithReadOnly(bufferUpdateMap)
            .ForEach(
            (
                Entity entity,
                int entityInQueryIndex,
                ref SpriteBufferData data,
                ref DynamicBuffer<SpriteIndexBuffer> indexBuffer,
                ref DynamicBuffer<SpriteColorBuffer> colorBuffer,
                ref DynamicBuffer<SpriteEntityBuffer> entityBuffer,
                ref DynamicBuffer<SpriteTransformBuffer> transformBuffer
            ) =>
            {
                if (entityBuffer.Capacity < bufferLengthMinimumCapacity ||
                    entityBuffer.Length / entityBuffer.Capacity > bufferLengthToCapacityThreshold)
                {
                    int capacity = math.max(bufferLengthMinimumCapacity, math.ceilpow2(entityBuffer.Capacity + 1));
                    indexBuffer.EnsureCapacity(capacity);
                    colorBuffer.EnsureCapacity(capacity);
                    entityBuffer.EnsureCapacity(capacity);
                    transformBuffer.EnsureCapacity(capacity);
                }

                NativeParallelHashSet<Entity> done = new NativeParallelHashSet<Entity>(entityBuffer.Length, Allocator.Temp);
                int i = 0;

                while (i < entityBuffer.Length)
                {
                    var spriteEntity = entityBuffer[i].entity;
                    //remove all duplicates, non existent sprite entities and sprite entities that use different buffer
                    if (done.Contains(spriteEntity) ||
                        buffers.TryGetComponent(spriteEntity, out var spriteBuffer) == false ||
                        spriteBuffer.bufferEntity != entity)
                    {
                        indexBuffer.RemoveAtSwapBack(i);
                        colorBuffer.RemoveAtSwapBack(i);
                        entityBuffer.RemoveAtSwapBack(i);
                        transformBuffer.RemoveAtSwapBack(i);

                        data.length -= data.length.IsGreaterThan(i);
                    }
                    else
                    {
                        if (!sameFrustrum || bufferUpdateMap.ContainsKey(entity))
                        {
                            if (indexUpdateMap.TryGetValue(spriteEntity, out var index))
                            {
                                indexBuffer[i] = index.value;
                            }

                            if (colorUpdateMap.TryGetValue(spriteEntity, out var color))
                            {
                                colorBuffer.ElementAt(i).value.x = Encode(color.value);
                            }

                            if (paintUpdateMap.TryGetValue(spriteEntity, out var paint))
                            {
                                uint2 paintIndex = paint.value;

                                if (paintIndex.x >= paintCount)
                                    paintIndex.x = 0;

                                if (paintIndex.y >= paintCount)
                                    paintIndex.y = 0;

                                colorBuffer.ElementAt(i).value.yz = paintIndex;
                            }

                            if (transformUpdateMap.TryGetValue(spriteEntity, out var transform))
                            {
                                transformBuffer[i] = transform.value;
                            }

                            bool enabled = i < data.length;
                            bool visible = indexBuffer[i].value >= 0;

                            if (cull && visible)
                            {
                                visible = IsVisible(nextFrustrum, transformBuffer[i].matrix.c0.xyz);
                            }

                            if (enabled && !visible)
                            {
                                data.length--;

                                if (i != data.length)
                                {
                                    indexBuffer.Swap(i, data.length);
                                    colorBuffer.Swap(i, data.length);
                                    entityBuffer.Swap(i, data.length);
                                    transformBuffer.Swap(i, data.length);
                                }
                            }
                            else if (!enabled && visible)
                            {
                                if (i != data.length)
                                {
                                    indexBuffer.Swap(i, data.length);
                                    colorBuffer.Swap(i, data.length);
                                    entityBuffer.Swap(i, data.length);
                                    transformBuffer.Swap(i, data.length);
                                }

                                data.length++;
                            }
                        }

                        done.Add(spriteEntity);
                        i++;
                    }
                }

                //done.Dispose();
            })
            .ScheduleParallel();

            indexUpdateMap.Dispose(Dependency);
            colorUpdateMap.Dispose(Dependency);
            paintUpdateMap.Dispose(Dependency);
            transformUpdateMap.Dispose(Dependency);
            bufferUpdateMap.Dispose(Dependency);

            bufferSystem.AddJobHandleForProducer(Dependency);
            lastCullingFrustrum = nextCullingFrustrum;
        }

        /// <summary>
        /// Check if given 3D vector position is visible within given frustrum matrix
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [GenerateTestsForBurstCompatibility]
        public static bool IsVisible(float4x4 frustrum, float3 position)
        {
            float4 result = math.mul(frustrum, new float4(position, 1.0f));
            float3 point = result.xyz / -result.w;//normalize result, now screen center is x == 0 and y == 0, edges are within interval [-1,+1] and z < 0 is behind the camera
            return point.x > -1 && point.x < 1 && point.y > -1 && point.y < 1 && point.z > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float4x4 GetCameraCullingFrustrum(Camera camera)
        {
            return camera != null ? camera.projectionMatrix * camera.transform.worldToLocalMatrix : float4x4.zero;
        }

        /// <summary>
        /// Encode float4 with values in interval 0-1 to uint through byte shift
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [GenerateTestsForBurstCompatibility]
        public static uint Encode(float4 value)
        {
            uint4 remap = (uint4)math.round(0xff * value);
            uint pack = (remap.w << 24) | (remap.z << 16) | (remap.y << 8) | remap.x;
            return pack;
        }

        /// <summary>
        /// Decode uint to float4 with values in interval 0-1 through byte shift
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [GenerateTestsForBurstCompatibility]
        public static float4 Decode(uint u)
        {
            uint x = u & 0xff;
            uint y = (u >> 8) & 0xff;
            uint z = (u >> 16) & 0xff;
            uint w = (u >> 24) & 0xff;
            return new float4(x / 0xff, y / 0xff, z / 0xff, w / 0xff);
        }

        /// <summary>
        /// Create default rectangular sprite mesh
        /// </summary>
        public static Mesh Quad()
        {
            Mesh mesh = new Mesh();
            Vector3[] vertices = new Vector3[4];
            vertices[0] = new Vector3(0, 0, 0);
            vertices[1] = new Vector3(1, 0, 0);
            vertices[2] = new Vector3(0, 1, 0);
            vertices[3] = new Vector3(1, 1, 0);
            mesh.vertices = vertices;

            int[] triangles = new int[6];
            triangles[0] = 0;
            triangles[1] = 2;
            triangles[2] = 1;
            triangles[3] = 2;
            triangles[4] = 3;
            triangles[5] = 1;
            mesh.triangles = triangles;

            Vector3[] normals = new Vector3[4];
            normals[0] = -Vector3.forward;
            normals[1] = -Vector3.forward;
            normals[2] = -Vector3.forward;
            normals[3] = -Vector3.forward;
            mesh.normals = normals;

            Vector2[] uv = new Vector2[4];
            uv[0] = new Vector2(0, 0);
            uv[1] = new Vector2(1, 0);
            uv[2] = new Vector2(0, 1);
            uv[3] = new Vector2(1, 1);
            mesh.uv = uv;

            return mesh;
        }

#if UNITY_EDITOR

        public static void GetSpritesFromTexture2D(Texture2D texture, List<Sprite> sprites)
        {
            UnityEngine.Object[] objects = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(texture));
            sprites.Clear();

            if (objects != null)
            {
                for (int i = 0; i < objects.Length; ++i)
                {
                    if (objects[i] is Sprite sprite)
                    {
                        sprites.Add(sprite);
                    }
                }
            }
        }

        /// <summary>
        /// Extract sprite keyframes to provided list from animation clip
        /// </summary>
        public static void GetSpriteKeyframesFromAnimationClip(AnimationClip clip, List<SpriteKeyframe> spriteKeyframes)
        {
            if (clip != null && spriteKeyframes != null)
            {
                spriteKeyframes.Clear();
                float clipLength = clip.length;

                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                {
                    var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding);

                    for (int i = 0; i < keyframes.Length; ++i)
                    {
                        var keyframe = keyframes[i];

                        if (keyframe.value is Sprite sprite)
                        {
                            float time = i+1 < keyframes.Length ? keyframes[i+1].time : clipLength;
                            time -= keyframe.time;
                            spriteKeyframes.Add(new SpriteKeyframe { sprite = sprite, time = time });
                        }
                    }
                }
            }
        }

        public static void GetTransformsFromAnimationClip(AnimationClip clip, List<TransformKeyframe> spriteTransforms)
        {
            if (clip != null && spriteTransforms != null)
            {
                spriteTransforms.Clear();
                Dictionary<float, float4x2> data = new();
                float clipLength = clip.length;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var keyframes = AnimationUtility.GetEditorCurve(clip, binding).keys;

                    for (int i = 0; i < keyframes.Length; ++i)
                    {
                        var keyframe = keyframes[i];
                        
                        if (binding.type == typeof(UnityEngine.Transform))
                        {
                            float4x2 value = float4x2.zero;
                            
                            if (binding.propertyName == "m_LocalPosition.x")
                                value.c0.x = keyframe.value;

                            if (binding.propertyName == "m_LocalPosition.y")
                                value.c0.y = keyframe.value;

                            if (binding.propertyName == "m_LocalPosition.z")
                                value.c0.z = keyframe.value;

                            if (binding.propertyName == "m_LocalScale.x")
                                value.c0.w = keyframe.value;

                            if (binding.propertyName == "m_LocalScale.y")
                                value.c0.w = keyframe.value;

                            if (binding.propertyName == "m_LocalScale.x")
                                value.c0.w = keyframe.value;

                            if (binding.propertyName == "m_LocalRotation.x")
                                value.c1.x = keyframe.value;

                            if (binding.propertyName == "m_LocalRotation.y")
                                value.c1.y = keyframe.value;

                            if (binding.propertyName == "m_LocalRotation.z")
                                value.c1.z = keyframe.value;

                            if (binding.propertyName == "m_LocalRotation.w")
                                value.c1.w = keyframe.value;

                            float3 eulerAngle = float3.zero;

                            if (binding.propertyName == "localEulerAnglesRaw.x")
                                eulerAngle.x = keyframe.value;

                            if (binding.propertyName == "localEulerAnglesRaw.y")
                                eulerAngle.y = keyframe.value;

                            if (binding.propertyName == "localEulerAnglesRaw.z")
                                eulerAngle.z = keyframe.value;

                            if (!eulerAngle.Equals(float3.zero))
                            {
                                value.c1 = quaternion.EulerXYZ(math.radians(eulerAngle)).value;
                            }

                            if (!value.Equals(float4x2.zero))
                            {
                                float time = i + 1 < keyframes.Length ? keyframes[i + 1].time : clipLength;
                                time -= keyframes[i].time;
                                
                                if (data.TryGetValue(time, out var entry))
                                {
                                    value += entry;
                                }

                                data[time] = value;
                            }
                        }
                    }
                }

                foreach (var entry in data)
                {
                    spriteTransforms.Add(new TransformKeyframe { transform = entry.Value, time = entry.Key });
                }
            }
        }

        public static void GetColorsFromAnimationClip(AnimationClip clip, List<ColorKeyframe> spriteColors)
        {
            if (clip != null && spriteColors != null)
            {
                spriteColors.Clear();
                Dictionary<float, float4> data = new();
                float clipLength = clip.length;

                foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                {
                    var keyframes = AnimationUtility.GetEditorCurve(clip, binding).keys;

                    for (int i = 0; i < keyframes.Length; ++i)
                    {
                        var keyframe = keyframes[i];
                        
                        if (binding.type == typeof(UnityEngine.SpriteRenderer))
                        {
                            float4 value = float4.zero;

                            if (binding.propertyName == "m_Color.r")
                                value.x = keyframe.value;

                            if (binding.propertyName == "m_Color.g")
                                value.y = keyframe.value;

                            if (binding.propertyName == "m_Color.b")
                                value.z = keyframe.value;

                            if (binding.propertyName == "m_Color.a")
                                value.w = keyframe.value;

                            if (!value.Equals(float4.zero))
                            {
                                float time = i + 1 < keyframes.Length ? keyframes[i + 1].time : clipLength;
                                time -= keyframes[i].time;

                                if (data.TryGetValue(time, out var entry))
                                {
                                    value += entry;
                                }

                                data[time] = value;
                            }
                        }
                    }
                }

                foreach (var keyframe in data)
                {
                    spriteColors.Add(new ColorKeyframe { color = keyframe.Value, time = keyframe.Key });
                }
            }
        }
#endif

        /// <summary>
        /// Create animation record. Animation must always be recorded using this method first before it can be used
        /// </summary>
        public void RecordAnimation(SpriteAnimationTemplate animation)
        {
            if (animation != null &&
                animation.frames != null &&
                animation.frames.Count > 0 &&
                !animation.guid.IsEmpty &&
                !IsRecorded(animation.guid))
            {
                var bufferEntity = EntityManager.CreateEntity(bufferArchetype);
                //EntityManager.SetName(bufferEntity, "SpriteSheetBuffer: " + animation.name);
                CreatePayload(bufferEntity, animation);

                if (animationBufferEntityMap.Count() + 1 >= animationBufferEntityMap.Capacity)
                {
                    animationBufferEntityMap.Capacity = animationBufferEntityMap.Capacity << 1;
                }

                animationBufferEntityMap.Add(animation.guid, bufferEntity);
                EntityManager.SetComponentData(bufferEntity, animation.GetData());
            }
        }

        /// <summary>
        /// Creates render payload data for the given render buffer entity from animation sprite array
        /// </summary>
        private void CreatePayload(Entity bufferEntity, SpriteAnimationTemplate animation)
        {
            var uvBuffer = EntityManager.GetBuffer<SpriteUvBuffer>(bufferEntity);
            var material = new Material(Shader.Find("Instanced/SpriteAnimation"));
            var texture = animation.frames[0].sprite.texture;
            int length = animation.frames.Count;
            material.mainTexture = texture;
            uvBuffer.EnsureCapacity(length);
            float w = texture.width;
            float h = texture.height;

            foreach (var frame in animation.frames)
            {
                float4 uv;
                uv.x = 1f / (w / frame.sprite.rect.width);
                uv.y = 1f / (h / frame.sprite.rect.height);
                uv.z = uv.x * (frame.sprite.rect.x / frame.sprite.rect.width);
                uv.w = uv.y * (frame.sprite.rect.y / frame.sprite.rect.height);
                uvBuffer.Add(uv);
            }

            BlobBuilder builder = new BlobBuilder(Allocator.TempJob);
            ref var root = ref builder.ConstructRoot<SpriteKeyframeBlob>();
            BlobBuilderArray<float4x2> transforms = builder.Allocate(ref root.transforms, animation.transforms.Count);
            BlobBuilderArray<float4> colors = builder.Allocate(ref root.colors, animation.colors.Count);
            BlobBuilderArray<float> spriteFrames = builder.Allocate(ref root.spriteFrames, length);
            BlobBuilderArray<float> transformFrames = builder.Allocate(ref root.spriteFrames, animation.transforms.Count);
            BlobBuilderArray<float> colorFrames = builder.Allocate(ref root.spriteFrames, animation.colors.Count);

            for (int i = 0; i < animation.transforms.Count; ++i)
            {
                TransformKeyframe keyframe = animation.transforms[i];
                transforms[i] = keyframe.transform;
                transformFrames[i] = keyframe.time;
            }

            for (int i = 0; i < animation.colors.Count; ++i)
            {
                ColorKeyframe keyframe = animation.colors[i];
                colors[i] = keyframe.color;
                colorFrames[i] = keyframe.time;
            }

            for (int i = 0; i < length; ++i)
            {
                spriteFrames[i] = animation.frames[i].time;
            }

            var spriteFrameBlobReference = builder.CreateBlobAssetReference<SpriteKeyframeBlob>(Allocator.Persistent);
            EntityManager.SetComponentData(bufferEntity, new SpriteKeyframeData(spriteFrameBlobReference));
            builder.Dispose();

            SpriteAnimationRenderPayload payload = new SpriteAnimationRenderPayload(material, bufferEntity, length, spriteFrameBlobReference);
            payload.SetData(uvBufferName, uvBuffer.Reinterpret<float4>().AsNativeArray(), 0, false);
            payload.SetData(paintBufferName, paints.AsArray());
            bufferPayloadMap.Add(bufferEntity, payload);
        }

        /// <summary>
        /// Check if given animation guid is already recorded. If not, it cannot be used
        /// </summary>
        public bool IsRecorded(HashGuid guid) => animationBufferEntityMap.ContainsKey(guid);

        /// <summary>
        /// Create sprite sheet animation instance using command buffer. Requires to pass in the SpriteSheetRenderer.spriteArchetype and SpriteSheetRenderer.InstantiateSpriteData
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Entity Instantiate(EntityCommandBuffer commandBuffer, EntityArchetype spriteArchetype, ref InstantiateSpriteData instance)
        {
            var e = commandBuffer.CreateEntity(spriteArchetype);
            commandBuffer.SetComponent(e, instance.animation);
            commandBuffer.SetComponent(e, instance.transform);
            commandBuffer.SetComponent(e, instance.movement);
            commandBuffer.SetComponent(e, instance.rotation);
            commandBuffer.SetComponent(e, instance.color);
            commandBuffer.SetComponent(e, instance.state);
            commandBuffer.SetComponent(e, instance.parent);
            return e;
        }

        /// <summary>
        /// Create sprite sheet animation instance using parallel command buffer. Requires to pass in the SpriteSheetRenderer.spriteArchetype and SpriteSheetRenderer.InstantiateSpriteData
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Entity Instantiate(EntityCommandBuffer.ParallelWriter parallelBuffer, int entityInQueryIndex, EntityArchetype spriteArchetype, ref InstantiateSpriteData instance)
        {
            var e = parallelBuffer.CreateEntity(entityInQueryIndex, spriteArchetype);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.animation);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.transform);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.movement);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.rotation);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.color);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.state);
            parallelBuffer.SetComponent(entityInQueryIndex, e, instance.parent);
            return e;
        }

        /// <summary>
        /// Set sprite sheet animation on the main thread to the given animation, automatically recording the animation if necessary
        /// </summary>
        public void SetAnimation(Entity e, SpriteAnimationTemplate animation)
        {
            if (!EntityManager.Exists(e) || animation == null)
                return;

            if (!IsRecorded(animation.guid))
                RecordAnimation(animation);

            EntityManager.SetComponentData(e, new SpriteAnimation(animation.guid));
        }

        /// <summary>
        /// Set sprite sheet animation by animation unique guid identifier using command buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetAnimation(EntityCommandBuffer commandBuffer, Entity e, HashGuid guid)
        {
            commandBuffer.SetComponent(e, new SpriteAnimation(guid));
        }

        /// <summary>
        /// Set sprite sheet animation by animation unique guid identifier using parallel command buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetAnimation(EntityCommandBuffer.ParallelWriter parallelBuffer, int entityInQueryIndex, Entity e, HashGuid guid)
        {
            parallelBuffer.SetComponent(entityInQueryIndex, e, new SpriteAnimation(guid));
        }

        /// <summary>
        /// Set sprite sheet animation play speed using command buffer. Zero or negative value will reset play speed to default (1)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPlaySpeed(EntityCommandBuffer commandBuffer, Entity e, float speed)
        {
            commandBuffer.SetComponent(e, new SpritePlaySpeed(speed));
        }

        /// <summary>
        /// Set sprite sheet animation play speed using parallel command buffer. Zero or negative value will reset play speed to default (1)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetPlaySpeed(EntityCommandBuffer.ParallelWriter parallelBuffer, int entityInQueryIndex, Entity e, float speed)
        {
            parallelBuffer.SetComponent(entityInQueryIndex, e, new SpritePlaySpeed(speed));
        }

        /// <summary>
        /// Remove sprite sheet animation instance on the main thread
        /// </summary>
        public void Remove(Entity entity)
        {
            if (EntityManager.Exists(entity))
            {
                EntityManager.SetComponentData(entity, new SpriteAnimation(HashGuid.Empty));
            }
        }

        /// <summary>
        /// Remove sprite sheet animation instance using command buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove(EntityCommandBuffer commandBuffer, Entity entity)
        {
            commandBuffer.SetComponent(entity, new SpriteAnimation(HashGuid.Empty));
        }

        /// <summary>
        /// Remove sprite sheet animation instance using parallel command buffer
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Remove(EntityCommandBuffer.ParallelWriter parallelBuffer, int entityInQueryIndex, Entity entity)
        {
            parallelBuffer.SetComponent(entityInQueryIndex, entity, new SpriteAnimation(HashGuid.Empty));
        }

        public int SetPaint(PaintConfig paint)
        {
            int index = GetPaintIndex(paint);

            if (index >= 0)
                return index;

            paints.Add(paint);
            UpdatePaints();
            return paints.Length - 1;
        }

        public int GetPaintIndex(PaintConfig paint)
        {
            float4x3 temp = paint;
            
            for (int i = 0; i < paints.Length; i++)
            {
                if (temp.Equals(paints[i]))
                    return i;
            }

            return -1;
        }

        public void ClearAllPaints()
        {
            paints.Clear();
            paints.Add(float4x3.zero);
            UpdatePaints();
        }

        private void UpdatePaints()
        {
            foreach (var payload in bufferPayloadMap.Values)
            {
                payload.SetData(paintBufferName, paints.AsArray());
            }
        }

        /// <summary>
        /// Clean up all the recorded animation data, returning system to its default state
        /// </summary>
        public void CleanBuffers()
        {
            foreach (var info in bufferPayloadMap)
            {
                info.Value.Dispose();
            }

            bufferPayloadMap.Clear();
            animationBufferEntityMap.Clear();
        }
    }

    [BurstCompile]
    struct RecordSpriteDataChangeJob<T> : IJobChunk where T : unmanaged, IComponentData
    {
        [ReadOnly] public EntityTypeHandle entityTypeHandle;
        [ReadOnly] public ComponentTypeHandle<T> componentTypeHandle;
        [ReadOnly] public ComponentTypeHandle<SpriteBuffer> bufferTypeHandle;
        [NativeDisableContainerSafetyRestriction] public NativeParallelHashMap<Entity, T>.ParallelWriter componentChanges;
        [NativeDisableContainerSafetyRestriction] public NativeParallelHashMap<Entity, bool>.ParallelWriter bufferChanges;
        public uint lastSystemVersion;

        [BurstCompile]
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var componentChanged = chunk.DidChange(ref componentTypeHandle, lastSystemVersion);
            var chunkChanged = chunk.DidOrderChange(lastSystemVersion);

            if (componentChanged || chunkChanged)
            {
                var entities = chunk.GetNativeArray(entityTypeHandle);
                var buffers = chunk.GetNativeArray(ref bufferTypeHandle);
                var components = chunk.GetNativeArray(ref componentTypeHandle);

                for (int i = 0; i < chunk.Count; ++i)
                {
                    componentChanges.TryAdd(entities[i], components[i]);
                    bufferChanges.TryAdd(buffers[i].bufferEntity, true);
                }
            }
        }
    }

    public static partial class Extensions
    {
        /// <summary>
        /// Simple entry swap by indexes for the DynamicBuffer struct
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Swap<T>(this DynamicBuffer<T> list, int a, int b) where T : unmanaged
        {
            a = Branchless.Clamp(a, 0, list.Length - 1);
            b = Branchless.Clamp(b, 0, list.Length - 1);
            T value = list[a];
            list[a] = list[b];
            list[b] = value;
        }

        /// <summary>
        /// Check if the integer value is within the specified range of [min, max)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool InRange(this int value, int min, int max)
        {
            return value >= min && value < max;
        }
    }
}