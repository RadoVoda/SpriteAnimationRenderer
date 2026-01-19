using System;
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;

namespace DOTSSpriteAnimation
{
    public class SpriteAnimationRenderPayload : IDisposable
    {
        /// <summary>
        /// entity containing animation data
        /// </summary>
        public Entity bufferEntity { get; private set; }
        /// <summary>
        /// number of animation instances to draw
        /// </summary>
        public uint count { get { return args[1]; } set { SetCount(value); } }
        /// <summary>
        /// number of sprite frames in this animation
        /// </summary>
        public int length { get; private set; }

        private struct AsyncBufferEntry
        {
            public ComputeBuffer buffer;
            public AsyncGPUReadbackRequest request;
        }

        private BlobAssetReference<SpriteKeyframeBlob> spriteBlob;
        private List<AsyncBufferEntry> asyncBuffers = new();
        private Dictionary<string, ComputeBuffer> activeBuffers = new();
        private ComputeBuffer argsBuffer;
        private uint[] args;
        private Material material;
        private bool supportAsync;

        public SpriteAnimationRenderPayload(Material material, Entity bufferEntity, int length, BlobAssetReference<SpriteKeyframeBlob> frameBlob)
        {
            this.material = material;
            this.length = length;
            this.bufferEntity = bufferEntity;
            this.spriteBlob = frameBlob;
            args = new uint[5] { 6, 0, 0, 0, 0 };
            argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
            supportAsync = SystemInfo.supportsAsyncGPUReadback;
        }

        public void SetCount(uint c)
        {
            if (args[1] != c)
            {
                args[1] = c;
                argsBuffer.SetData(args);
            }
        }

        public bool IsValid() => material != null && length > 0 && bufferEntity != Entity.Null;

        public unsafe void SetData<T>(string name, NativeArray<T> array, int length = 0, bool asyncWrite = true) where T : unmanaged
        {
            if (name == null || name.Length == 0 || array.Length == 0)
                return;

            if (length <= 0 || length > array.Length)
                length = array.Length;

            if (asyncWrite && supportAsync)
            {
                var buffer = GetAsyncBuffer(length, sizeof(T));
                var write = buffer.BeginWrite<T>(0, length);
                NativeArray<T>.Copy(array, write, length);
                buffer.EndWrite<T>(length);
                material.SetBuffer(name, buffer);
                activeBuffers[name] = buffer;
            }
            else
            {
                if (activeBuffers.TryGetValue(name, out var buffer) == false || buffer.count < length)
                {
                    buffer?.Dispose();
                    buffer = new ComputeBuffer(length, sizeof(T), ComputeBufferType.Default, ComputeBufferMode.Immutable);
                    activeBuffers[name] = buffer;
                }

                buffer.SetData(array);
                material.SetBuffer(name, buffer);
            }
        }

        private ComputeBuffer GetAsyncBuffer(int count, int stride)
        {
            count = math.ceilpow2(count + 1);

            for (int i = 0; i < asyncBuffers.Count; ++i)
            {
                var entry = asyncBuffers[i];

                if (entry.request.done && entry.buffer.stride == stride)
                {
                    if (entry.buffer.count < count)
                    {
                        entry.buffer.Dispose();
                        asyncBuffers.RemoveAtSwapBack(i);
                        i--;
                    }
                    else
                    {
                        entry.request = AsyncGPUReadback.Request(entry.buffer);
                        asyncBuffers[i] = entry;
                        return entry.buffer;
                    }
                }
            }

            var buffer = new ComputeBuffer(count, stride, ComputeBufferType.Default, ComputeBufferMode.SubUpdates);
            var newEntry = new AsyncBufferEntry { buffer = buffer, request = AsyncGPUReadback.Request(buffer) };
            asyncBuffers.Add(newEntry);
            return buffer;
        }

        public void Draw(Mesh mesh, Bounds bounds)
        {
            Graphics.DrawMeshInstancedIndirect(mesh, 0, material, bounds, argsBuffer);
        }

        public void Dispose()
        {
            foreach (var entry in activeBuffers)
            {
                entry.Value?.Release();
            }

            foreach (var entry in asyncBuffers)
            {
                entry.buffer?.Release();
            }

            activeBuffers.Clear();
            asyncBuffers.Clear();
            argsBuffer?.Release();
            argsBuffer = null;

            if (spriteBlob.IsCreated)
                spriteBlob.Dispose();
        }
    }
}