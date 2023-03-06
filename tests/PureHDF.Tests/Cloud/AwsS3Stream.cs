﻿using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace PureHDF.Tests
{
    public class AwsS3Stream : Stream, IDisposable
    {
        private readonly ConcurrentDictionary<long, IMemoryOwner<byte>> _cache = new();
        private readonly int _cacheSlotSize;
        private readonly string _bucketName;
        private readonly string _key;
        private readonly AmazonS3Client _client;

        private readonly ThreadLocal<long> _position = new();

        public AwsS3Stream(AmazonS3Client client, string bucketName, string key, int cacheSlotSize = 1 * 1024 * 1024)
        {
            if (cacheSlotSize <= 0)
                throw new Exception("Cache slot size must be > 0");

            _client = client;
            _bucketName = bucketName;
            _key = key;
            _cacheSlotSize = cacheSlotSize;

            // https://registry.opendata.aws/nrel-pds-wtk/
            Length = client
                .GetObjectMetadataAsync(bucketName, key)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult()
                .ContentLength;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position 
        { 
            get => _position.Value; 
            set => _position.Value = value; 
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var valueTask = ReadCoreAsync(buffer.AsMemory(offset, count), useAsync: false);

            if (!valueTask.IsCompleted)
                throw new Exception("This should never happen.");

            return valueTask
                .GetAwaiter()
                .GetResult();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            return ReadCoreAsync(buffer, useAsync: true, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:

                    _position.Value = offset;

                    if (!(0 <= _position.Value && _position.Value < Length))
                        throw new Exception("The offset exceeds the stream length.");

                    return _position.Value;

                case SeekOrigin.Current:

                    _position.Value += offset;

                    if (!(0 <= _position.Value && _position.Value < Length))
                        throw new Exception("The offset exceeds the stream length.");

                    return _position.Value;
            }

            throw new Exception($"Seek origin '{origin}' is not supported.");
        }

        public override void SetLength(long value) => throw new NotImplementedException();

        public override void Flush() => throw new NotImplementedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var (_, cacheEntry) in _cache)
                {
                    cacheEntry.Dispose();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, bool useAsync, CancellationToken cancellationToken = default)
        {
            // TODO issue parallel requests
            // TODO do not cache dataset data
            var s3UpperLength = Math.Max(_cacheSlotSize, buffer.Length);
            var s3Remaining = Length - _position.Value;
            var s3ActualLength = (int)Math.Min(s3UpperLength, s3Remaining);
            var s3Processed = 0;
            var s3StartIndex = -1L;
            var remainingBuffer = buffer;

            bool loadFromS3;

            while (s3Processed < s3ActualLength)
            {
                var currentIndex = (_position.Value + s3Processed) / _cacheSlotSize;
                loadFromS3 = false;

                // determine if data is cached
                var owner = _cache.GetOrAdd(currentIndex, currentIndex =>
                {
                    var owner = MemoryPool<byte>.Shared.Rent(_cacheSlotSize);

                    // first index for which data will be requested
                    if (s3StartIndex == -1)
                        s3StartIndex = currentIndex;

                    loadFromS3 = true;

                    return owner;
                });

                if (!loadFromS3 /* i.e. data is in cache */)
                {
                    // is there a not yet loaded range of data?
                    if (s3StartIndex != -1)
                    {
                        var s3EndIndex = currentIndex + 1;
                        remainingBuffer = await LoadFromS3ToCacheAndBufferAsync(s3StartIndex, s3EndIndex, remainingBuffer, useAsync: useAsync, cancellationToken);
                        s3StartIndex = -1;
                    }

                    // copy from cache
                    remainingBuffer = CopyFromCacheToBuffer(currentIndex, owner, remainingBuffer);
                }

                s3Processed += _cacheSlotSize;
            }

            // TODO code duplication
            // is there a not yet loaded range of data?
            if (s3StartIndex != -1)
            {
                var s3EndIndex = s3StartIndex + s3ActualLength / _cacheSlotSize;
                remainingBuffer = await LoadFromS3ToCacheAndBufferAsync(s3StartIndex, s3EndIndex, remainingBuffer, useAsync: useAsync, cancellationToken);
                s3StartIndex = -1;
            }

            return buffer.Length;
        }
    
        private async Task<Memory<byte>> LoadFromS3ToCacheAndBufferAsync(
            long s3StartIndex, 
            long s3EndIndex, 
            Memory<byte> remainingBuffer, 
            bool useAsync, 
            CancellationToken cancellationToken)
        {
            // get S3 stream
            var s3Start = s3StartIndex * _cacheSlotSize;
            var s3End = Math.Min(s3EndIndex * _cacheSlotSize, Length);

            var request = new GetObjectRequest()
            {
                BucketName = _bucketName,
                Key = _key,
                ByteRange = new ByteRange(s3Start, s3End)
            };

            var task = _client.GetObjectAsync(request, cancellationToken);

            var response = useAsync
                ? await task.ConfigureAwait(false)
                : task.GetAwaiter().GetResult();

            var stream = response.ResponseStream;

            // copy
            for (long currentIndex = s3StartIndex; currentIndex < s3EndIndex; currentIndex++)
            {
                var owner = _cache.GetOrAdd(currentIndex, _ => throw new Exception("This should never happen."));

                // copy to cache
                var memory = owner.Memory[..(int)Math.Min(_cacheSlotSize, Length - Position)];

#if NET7_0_OR_GREATER
                if (useAsync)
                    await stream.ReadExactlyAsync(memory, cancellationToken);

                else
                    stream.ReadExactly(memory.Span);
#else
                var slicedBuffer = memory;

                while (slicedBuffer.Length > 0)
                {
                    var readBytes = useAsync
                        ? await stream.ReadAsync(slicedBuffer, cancellationToken)
                        : stream.Read(slicedBuffer.Span);

                    slicedBuffer = slicedBuffer[readBytes..];
                };
#endif

                // copy to request buffer
                remainingBuffer = CopyFromCacheToBuffer(currentIndex, owner, remainingBuffer);
            }

            return remainingBuffer;
        }

        private Memory<byte> CopyFromCacheToBuffer(long currentIndex, IMemoryOwner<byte> owner, Memory<byte> remainingBuffer)
        {
            var s3Position = currentIndex * _cacheSlotSize;

            var cacheSlotOffset = _position.Value > s3Position
                ? (int)(_position.Value - s3Position)
                : 0;

            var remainingCacheSlotSize = _cacheSlotSize - cacheSlotOffset;

            var slicedMemory = owner.Memory
                .Slice(cacheSlotOffset, Math.Min(remainingCacheSlotSize, remainingBuffer.Length));

            slicedMemory.Span.CopyTo(remainingBuffer.Span);

            remainingBuffer = remainingBuffer[slicedMemory.Length..];
            _position.Value += slicedMemory.Length;

            return remainingBuffer;
        }
    }
}
