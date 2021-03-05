﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace HDF5.NET
{
    // https://support.hdfgroup.org/HDF5/doc/H5.user/Caching.html
    // https://support.hdfgroup.org/HDF5/faq/perfissues.html
    public class SimpleChunkCache : IChunkCache
    {
        #region Records

        private record ChunkInfo(DateTime LastAccess, Memory<byte> Chunk)
        {
            public DateTime LastAccess { get; set; }
        }

        #endregion

        #region Fields

        private Dictionary<ulong, ChunkInfo> _chunkInfoMap;

        #endregion

        #region Constructors

        public SimpleChunkCache(int chunkSlotCount = 521, ulong byteCount = 1024 * 1024/*, double w0 = 0.75*/)
        {
            if (chunkSlotCount < 0)
                throw new Exception("The chunk slot count parameter must be >= 0.");

            //if (!(0 <= w0 && w0 <= 1))
            //    throw new ArgumentException("The parameter w0 must be in the range of 0..1 (inclusive).");

            this.ChunkSlotCount = chunkSlotCount;
            this.ByteCount = byteCount;

            _chunkInfoMap = new Dictionary<ulong, ChunkInfo>();
        }

        #endregion

        #region Properties
        public int ChunkSlotCount { get; init; }

        public int ConsumedSlots => _chunkInfoMap.Count;

        public ulong ByteCount { get; init; }

        public ulong ConsumedBytes { get; private set; }

        #endregion

        #region Methdos

        public Memory<byte> GetChunk(ulong index, Func<Memory<byte>> chunkLoader)
        {
            if (_chunkInfoMap.TryGetValue(index, out var chunkInfo))
            {
                chunkInfo.LastAccess = DateTime.Now;
            }
            else
            {
                chunkInfo = new ChunkInfo(LastAccess: DateTime.Now, chunkLoader.Invoke());
                var chunk = chunkInfo.Chunk;

                if ((ulong)chunk.Length <= this.ByteCount)
                {
                    while (_chunkInfoMap.Count >= this.ChunkSlotCount || this.ByteCount - this.ConsumedBytes < (ulong)chunk.Length)
                    {
                        this.Preempt();
                    }

                    this.ConsumedBytes += (ulong)chunk.Length;
                    _chunkInfoMap[index] = chunkInfo;
                }
            }

            return chunkInfo.Chunk;
        }

        private void Preempt()
        {
            var entry = _chunkInfoMap
                .OrderBy(current => current.Value.LastAccess)
                .FirstOrDefault();

            this.ConsumedBytes -= (ulong)entry.Value.Chunk.Length;
            _chunkInfoMap.Remove(entry.Key);
        }

        #endregion
    }
}