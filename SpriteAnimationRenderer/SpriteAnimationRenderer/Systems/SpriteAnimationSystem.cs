
using Unity.Entities;

namespace DOTSSpriteAnimation
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

            Dependency = new SpriteEntityAnimationJob
            {
                commands = bufferSystem.CreateCommandBuffer().AsParallelWriter(),
                deltaTime = deltaTime
            }.ScheduleParallel(Dependency);

            Dependency = new SpriteEntityTransformJob
            {
                commands = bufferSystem.CreateCommandBuffer().AsParallelWriter(),
                deltaTime = deltaTime
            }.ScheduleParallel(Dependency);

            bufferSystem.AddJobHandleForProducer(Dependency);
        }
    }
}
