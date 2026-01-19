
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;

namespace DOTSSpriteAnimation
{
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

    [BurstCompile]
    public partial struct CheckSpriteLifetimeJob : IJobEntity
    {
        public EntityStorageInfoLookup storage;
        public EntityCommandBuffer.ParallelWriter commands;

        [BurstCompile]
        public void Execute
        (
            Entity entity,
            [ChunkIndexInQuery] int entityInQueryIndex,
            in SpriteLifetime life
        )
        {
            if (life.entity != Entity.Null && storage.Exists(life.entity) == false)
            {
                commands.DestroyEntity(entityInQueryIndex, entity);
            }
        }
    }

    [BurstCompile]
    public partial struct UpdateSpriteEntitiesJob : IJobEntity
    {
        public EntityStorageInfoLookup storage;
        public EntityCommandBuffer.ParallelWriter commands;
        public uint paintCount;
        [ReadOnly] public NativeParallelHashMap<HashGuid, Entity> animBufferMap;
        [ReadOnly] public ComponentLookup<SpriteAnimationData> sheets;
        [ReadOnly] public ComponentLookup<SpriteKeyframeData> frames;

        [BurstCompile]
        public void Execute
        (
            Entity entity,
            [ChunkIndexInQuery] int entityInQueryIndex,
            in SpriteBuffer buffer,
            in SpriteAnimation request,
            in SpriteLifetime life,
            in SpriteColor color,
            in SpritePaint paint,
            in SpriteTransform matrix
        )
        {
            if (life.entity != Entity.Null && storage.Exists(life.entity) == false)
            {
                return;
            }

            if (animBufferMap.TryGetValue(request.guid, out var bufferEntity) == false ||
                (buffer.bufferEntity != Entity.Null && storage.Exists(buffer.bufferEntity) == false) ||
                (buffer.bufferEntity != Entity.Null && sheets.HasComponent(buffer.bufferEntity) == false))
            {
                commands.DestroyEntity(entityInQueryIndex, entity);
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

                commands.SetComponent(entityInQueryIndex, entity, new SpriteBuffer(bufferEntity));
                commands.SetComponent(entityInQueryIndex, entity, new SpritePlayback(0, lifetime, sheet.length, sheet.mode));
                commands.SetComponent(entityInQueryIndex, entity, new SpriteIndex(sheet.start));
                commands.SetComponent(entityInQueryIndex, entity, new SpriteKeyframeClocks(frame));
                commands.SetComponent(entityInQueryIndex, entity, frame);

                commands.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteIndexBuffer(sheet.start));
                commands.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteColorBuffer(color.value, paintIndex));
                commands.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteTransformBuffer(matrix));
                commands.AppendToBuffer(entityInQueryIndex, bufferEntity, new SpriteEntityBuffer(entity));
            }
        }
    }

    [BurstCompile]
    public partial struct UpdateSpriteEntityBuffersJob : IJobEntity
    {
        public bool sameFrustrum;
        public bool cull;
        public uint paintCount;
        public float4x4 nextFrustrum;
        [ReadOnly] public ComponentLookup<SpriteBuffer> buffers;
        [ReadOnly] public NativeParallelHashMap<Entity, SpriteIndex> indexUpdateMap;
        [ReadOnly] public NativeParallelHashMap<Entity, SpriteColor> colorUpdateMap;
        [ReadOnly] public NativeParallelHashMap<Entity, SpritePaint> paintUpdateMap;
        [ReadOnly] public NativeParallelHashMap<Entity, SpriteTransform> transformUpdateMap;
        [ReadOnly] public NativeParallelHashMap<Entity, bool> bufferUpdateMap;

        [BurstCompile]
        public void Execute
        (
            Entity entity,
            [ChunkIndexInQuery] int entityInQueryIndex,
            ref SpriteBufferData data,
            ref DynamicBuffer<SpriteIndexBuffer> indexBuffer,
            ref DynamicBuffer<SpriteColorBuffer> colorBuffer,
            ref DynamicBuffer<SpriteEntityBuffer> entityBuffer,
            ref DynamicBuffer<SpriteTransformBuffer> transformBuffer
        )
        {
            if (entityBuffer.Capacity < SpriteAnimationRenderer.bufferLengthMinimumCapacity ||
                    (float)entityBuffer.Length > entityBuffer.Capacity * SpriteAnimationRenderer.bufferLengthToCapacityThreshold)
            {
                int capacity = math.max(SpriteAnimationRenderer.bufferLengthMinimumCapacity, math.ceilpow2(entityBuffer.Capacity + 1));
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

                    if (data.length > i)
                    {
                        data.length--;
                    }
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
                            colorBuffer.ElementAt(i).value.x = SpriteAnimationRenderer.Encode(color.value);
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
                            visible = SpriteAnimationRenderer.IsVisible(nextFrustrum, transformBuffer[i].matrix.c0.xyz);
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
        }
    }

    [BurstCompile]
    public partial struct SpriteEntityAnimationJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter commands;
        public float deltaTime;
        
        [BurstCompile]
        public void Execute
        (
            Entity entity,
            [ChunkIndexInQuery] int entityInQueryIndex,
            ref SpritePlayback playback,
            ref SpriteIndex index,
            in SpritePlaySpeed speed,
            in SpriteState state,
            in SpriteKeyframeData frames
        )
        {
            bool visible = state.value.Match(SpriteState.ESpriteState.Show);

            if (!visible && index.value >= 0)
            {
                index.value = index.value == 0 ? int.MinValue : -index.value;
            }
            else if (visible && index.value < 0)
            {
                index.value = index.value == int.MinValue ? 0 : -index.value;
            }

            if (state.value.Match(SpriteState.ESpriteState.Life) && playback.life > 0f)
            {
                playback.life -= deltaTime;

                if (playback.life <= 0f)
                {
                    SpriteAnimationRenderer.Remove(commands, entityInQueryIndex, entity);
                    return;
                }
            }

            float frameTime = 1f;

            if (frames.IsValid && index.value.InRange(0, frames.data.spriteFrames.Length))
            {
                frameTime = frames.data.spriteFrames[index.value];
            }

            if (speed.value > 0f)
            {
                frameTime *= speed.value;
            }

            if (state.value.Match(SpriteState.ESpriteState.Play) && playback.time >= frameTime)
            {
                switch (playback.mode)
                {
                    case ESpritePlayMode.Once:
                        {
                            if (index.value < playback.length - 1)
                                index.value++;
                        }
                        break;

                    case ESpritePlayMode.Loop:
                        {
                            index.value++;

                            if (index.value >= playback.length)
                                index.value = 0;
                        }
                        break;

                    case ESpritePlayMode.Forward:
                        {
                            if (index.value < playback.length - 1)
                                index.value++;
                            else
                                playback.mode = ESpritePlayMode.Reverse;
                        }
                        break;

                    case ESpritePlayMode.Reverse:
                        {
                            if (index.value > 0)
                                index.value--;
                            else
                                playback.mode = ESpritePlayMode.Forward;
                        }
                        break;
                }

                playback.time = 0f;
            }
            else
            {
                playback.time += deltaTime;
            }
        }
    }

    [BurstCompile]
    public partial struct SpriteEntityTransformJob : IJobEntity
    {
        public EntityCommandBuffer.ParallelWriter commands;
        public float deltaTime;

        [BurstCompile]
        public void Execute
        (
            ref SpriteTransform transform,
            ref SpriteMovement movement,
            ref SpriteKeyframeClocks clocks,
            ref SpriteRotation rotation,
            ref SpriteEulerAngle eulerAngle,
            ref SpriteColor color,
            in SpriteKeyframeData frames
        )
        {
            if (movement.trajectory != SpriteMovement.ESpriteTrajectory.None)
            {
                float fraction = movement.duration > 0f ? movement.progress / movement.duration : 1f;

                if (fraction == 0f)
                {
                    movement.origin = transform.position;
                }

                if (fraction < 1f)
                {
                    movement.progress += deltaTime;

                    if (movement.trajectory == SpriteMovement.ESpriteTrajectory.Curve)
                    {
                        //parabola from 0 - 1 with max at 0.5 == (1-(x*2-1)^2)
                        float3 target = movement.target;
                        target.y += (1f - math.pow((fraction * 2f) - 1f, 2f)) * math.distance(movement.origin, movement.target) * 0.5f;
                        transform.position = math.lerp(movement.origin, target, fraction);
                    }
                    else if (movement.trajectory == SpriteMovement.ESpriteTrajectory.Line)
                    {
                        transform.position = math.lerp(movement.origin, movement.target, fraction);
                    }
                }
                else if (transform.position.Equals(movement.target) == false)
                {
                    movement.progress = movement.duration;
                    transform.position = movement.target;
                }
            }

            if (rotation.policy != SpriteRotation.ESpriteRotation.None)
            {
                float fraction = rotation.duration > 0f ? rotation.progress / rotation.duration : 1f;

                if (fraction == 0f)
                {
                    rotation.origin = eulerAngle.value;
                }

                if (fraction < 1f)
                {
                    switch (rotation.policy)
                    {
                        case SpriteRotation.ESpriteRotation.Absolute:
                            {
                                float3 delta = math.lerp(rotation.origin, rotation.target, fraction);
                                rotation.progress += deltaTime;
                                transform.rotation = quaternion.EulerXYZ(math.radians(delta));
                                eulerAngle.value = delta;
                            }
                            break;
                        case SpriteRotation.ESpriteRotation.Relative:
                            {
                                float3 delta = rotation.target / rotation.duration * deltaTime;
                                rotation.progress += deltaTime;
                                quaternion q = quaternion.EulerXYZ(math.radians(delta));
                                transform.rotation = math.mul(transform.rotation, q);
                                rotation.origin = math.mul(q, rotation.origin);
                                eulerAngle.value = delta;
                            }
                            break;
                        case SpriteRotation.ESpriteRotation.Spin:
                            {
                                float3 delta = rotation.target / rotation.duration * deltaTime;
                                quaternion q = quaternion.EulerXYZ(math.radians(delta));
                                transform.rotation = math.mul(transform.rotation, q);
                                rotation.origin = math.mul(q, rotation.origin);
                                eulerAngle.value += delta;
                                eulerAngle.value %= 360;
                            }
                            break;
                    }
                }
                else if (rotation.policy != SpriteRotation.ESpriteRotation.Spin && rotation.origin.Equals(rotation.target) == false)
                {
                    transform.rotation = quaternion.EulerXYZ(math.radians(rotation.target));
                    rotation.origin = rotation.target;
                    eulerAngle.value = rotation.target;
                }
            }
            //apply transform keyframe data from animation template
            if (frames.IsValid && clocks.transformKeyframeIndex.InRange(0, frames.data.transforms.Length))
            {
                if (clocks.transformKeyframeTime >= frames.data.transformFrames[clocks.transformKeyframeIndex])
                {
                    clocks.transformKeyframeIndex++;
                    clocks.transformKeyframeIndex %= frames.data.transforms.Length;
                    clocks.transformKeyframeTime = 0f;
                }
                else
                {
                    clocks.transformKeyframeTime += deltaTime;
                }

                float4x2 frameTransforms = frames.data.transforms[clocks.transformKeyframeIndex];
                transform.position += frameTransforms.c0.xyz;
                transform.scale = frameTransforms.c0.w;
                transform.rotation.value *= frameTransforms.c1;
            }
            //apply color keyframe data from animation template
            if (frames.IsValid && clocks.colorKeyframeIndex.InRange(0, frames.data.colors.Length))
            {
                if (clocks.colorKeyframeTime >= frames.data.colorFrames[clocks.colorKeyframeIndex])
                {
                    clocks.colorKeyframeIndex++;
                    clocks.colorKeyframeIndex %= frames.data.colors.Length;
                    clocks.colorKeyframeTime = 0f;
                }
                else
                {
                    clocks.colorKeyframeTime += deltaTime;
                }

                color.value = frames.data.colors[clocks.colorKeyframeIndex];
            }
        }
    }
}
