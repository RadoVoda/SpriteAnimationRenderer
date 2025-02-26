
using Unity.Entities;
using Unity.Mathematics;
using Unity.Jobs;
using BurstEnums;

namespace SpriteAnimation
{
    public partial class SpriteAnimationSystem : SystemBase
    {
        private EndSimulationEntityCommandBufferSystem bufferSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            bufferSystem = World.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
        }

        protected override void OnUpdate()
        {
            var deltaTime = SystemAPI.Time.DeltaTime;
            var commandBuffer = bufferSystem.CreateCommandBuffer();
            var parallelBuffer = commandBuffer.AsParallelWriter();

            Entities
            .WithName("SpriteEntityAnimation")
            .ForEach(
            (
                Entity entity,
                int entityInQueryIndex,
                ref SpritePlayback playback,
                ref SpriteIndex index,
                in SpritePlaySpeed speed,
                in SpriteState state,
                in SpriteKeyframeData frames
            ) =>
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
                        SpriteAnimationRenderer.Remove(parallelBuffer, entityInQueryIndex, entity);
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
            })
            .ScheduleParallel();

            Entities
                .WithName("SpriteEntityTransform")
                .ForEach(
                (
                    ref SpriteTransform transform,
                    ref SpriteMovement movement,
                    ref SpriteKeyframeClocks clocks,
                    ref SpriteRotation rotation,
                    ref SpriteEulerAngle eulerAngle,
                    ref SpriteColor color,
                    in SpriteKeyframeData frames
                ) =>
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
                })
                .ScheduleParallel();

            bufferSystem.AddJobHandleForProducer(Dependency);
        }
    }
}
