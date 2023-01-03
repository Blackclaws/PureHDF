using System.Reflection;

namespace HDF5.NET
{
    public partial class H5Dataset : H5AttributableObject
    {
        #region Properties

        public H5File File { get; }

        public H5Dataspace Space
        {
            get
            {
                if (_space is null)
                    _space = new H5Dataspace(InternalDataspace);

                return _space;
            }
        }

        public H5DataType Type
        {
            get
            {
                if (_type is null)
                    _type = new H5DataType(InternalDataType);

                return _type;
            }
        }

        public H5DataLayout Layout
        {
            get
            {
                if (_layout is null)
                    _layout = new H5DataLayout(InternalDataLayout);

                return _layout;
            }
        }

        public H5FillValue FillValue
        {
            get
            {
                if (_fillValue is null)
                    _fillValue = new H5FillValue(InternalFillValue);

                return _fillValue;
            }
        }

        #endregion

        #region Public

        public byte[] Read(
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default)
        {
            var result = Read<byte>(
                null,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: false);

            if (result is null)
                throw new Exception("The buffer is null. This should never happen.");

            return result;
        }

        public T[] Read<T>(
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default) where T : unmanaged
        {
            var result = Read<T>(
                null,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: false);

            if (result is null)
                throw new Exception("The buffer is null. This should never happen.");

            return result;
        }

        public IQueryable<T> AsQueryable<T>(
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default) where T : unmanaged
        {
            if (Space.Rank != 1)
                throw new Exception("Querying data only works for 1-dimensional datasets.");

            var provider = new QueryProvider<T>(
                datasetLength: Space.Dimensions[0],
                executor: fileSelection => Read<T>(fileSelection, memorySelection, memoryDims, datasetAccess));

            var queryable = new Queryable<T>(provider);

            return queryable;
        }

        public void Read<T>(
            Memory<T> buffer,
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default) where T : unmanaged
        {
            Read(
                buffer,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: false);
        }

        public T[] ReadCompound<T>(
           Func<FieldInfo, string>? getName = default,
           Selection? fileSelection = default,
           Selection? memorySelection = default,
           ulong[]? memoryDims = default,
           H5DatasetAccess datasetAccess = default) where T : struct
        {
            var data = Read<byte>(
                null,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: false);

            if (data is null)
                throw new Exception("The buffer is null. This should never happen.");

            if (getName is null)
                getName = fieldInfo => fieldInfo.Name;

            return H5ReadUtils.ReadCompound<T>(InternalDataType, data, Context.Superblock, getName);
        }

        public Dictionary<string, object?>[] ReadCompound(
           Selection? fileSelection = default,
           Selection? memorySelection = default,
           ulong[]? memoryDims = default,
           H5DatasetAccess datasetAccess = default)
        {
            var data = Read<byte>(
                null,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: false);

            if (data is null)
                throw new Exception("The buffer is null. This should never happen.");

            return H5ReadUtils.ReadCompound(InternalDataType, data, Context.Superblock);
        }

        public string[] ReadString(
            Selection? fileSelection = default,
            Selection? memorySelection = default,
            ulong[]? memoryDims = default,
            H5DatasetAccess datasetAccess = default)
        {
            var data = Read<byte>(
                null,
                fileSelection,
                memorySelection,
                memoryDims,
                datasetAccess,
                skipShuffle: false,
                skipTypeCheck: true);

            if (data is null)
                throw new Exception("The buffer is null. This should never happen.");

            return H5ReadUtils.ReadString(InternalDataType, data, Context.Superblock);
        }

        #endregion
    }
}
