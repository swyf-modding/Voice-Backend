using System;
using System.Runtime.InteropServices;

namespace ScamWYF.VoiceBackend.Ort
{
    internal enum OrtElementType
    {
        Float = 1,
        Int32 = 6,
        Int64 = 7,
    }

    /// <summary>One input or output tensor. Owns the OrtValue it wraps.</summary>
    internal sealed class OrtTensor : IDisposable
    {
        private IntPtr _value;

        private OrtTensor(IntPtr value, long[] shape, OrtElementType type)
        {
            _value = value;
            Shape = shape;
            Type = type;
        }

        internal IntPtr Value { get { return _value; } }
        internal long[] Shape { get; private set; }
        internal OrtElementType Type { get; private set; }

        internal long ElementCount
        {
            get
            {
                long total = 1;
                for (int i = 0; i < Shape.Length; i++) total *= Shape[i];
                return total;
            }
        }

        /// <summary>
        /// A float tensor. The data is copied once into an ORT-allocated buffer: pinning a managed array
        /// across a native call is the alternative, and a copy of a few megabytes is the cheaper risk.
        /// </summary>
        internal static OrtTensor Float(float[] data, long[] shape)
        {
            ExpectSize(data.Length, shape, "float");
            return Create(data, shape, OrtElementType.Float);
        }

        internal static OrtTensor Long(long[] data, long[] shape)
        {
            ExpectSize(data.Length, shape, "int64");
            return Create(data, shape, OrtElementType.Int64);
        }

        internal static OrtTensor Int(int[] data, long[] shape)
        {
            ExpectSize(data.Length, shape, "int32");
            return Create(data, shape, OrtElementType.Int32);
        }

        private static void ExpectSize(int actual, long[] shape, string what)
        {
            long expected = 1;
            for (int i = 0; i < shape.Length; i++) expected *= shape[i];
            if (expected != actual)
            {
                throw new ArgumentException(string.Format(
                    "{0} tensor wants {1} elements for shape [{2}] but got {3}.",
                    what, expected, string.Join(",", Array.ConvertAll(shape, x => x.ToString())), actual));
            }
        }

        private static OrtTensor Create(Array data, long[] shape, OrtElementType type)
        {
            OrtApi.Table api = OrtApi.Instance;
            IntPtr allocator;
            OrtApi.Check(api.Get<OrtApi.DGetAllocator>(OrtApi.GetAllocatorWithDefaultOptions)(out allocator),
                "GetAllocatorWithDefaultOptions");

            IntPtr[] dims = ToPointers(shape);
            IntPtr value;
            OrtApi.Check(api.Get<OrtApi.DCreateTensor>(OrtApi.CreateTensorAsOrtValue)(
                allocator, dims, new IntPtr(shape.Length), (int)type, out value), "CreateTensorAsOrtValue");

            IntPtr target;
            OrtApi.Check(api.Get<OrtApi.DGetPointer>(OrtApi.GetTensorMutableData)(value, out target),
                "GetTensorMutableData");

            int elementSize = type == OrtElementType.Float ? 4 : type == OrtElementType.Int32 ? 4 : 8;
            // Marshal.Copy has no float[]/long[] overload in the game's stripped corlib, so the data
            // goes through a byte buffer. One copy of a few megabytes is cheaper than a pinned handle
            // held across a native call.
            byte[] bytes = new byte[(long)data.Length * elementSize];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            Marshal.Copy(bytes, 0, target, bytes.Length);

            return new OrtTensor(value, shape, type);
        }

        /// <summary>Wraps an OrtValue the runtime produced, reading its real shape and type back from ORT.</summary>
        internal static OrtTensor FromOutput(IntPtr value)
        {
            OrtApi.Table api = OrtApi.Instance;
            IntPtr info;
            OrtApi.Check(api.Get<OrtApi.DGetTensorTypeAndShape>(OrtApi.GetTensorTypeAndShape)(value, out info),
                "GetTensorTypeAndShape");
            try
            {
                int elementType;
                OrtApi.Check(api.Get<OrtApi.DGetElementType>(OrtApi.GetTensorElementType)(info, out elementType),
                    "GetTensorElementType");

                IntPtr count;
                OrtApi.Check(api.Get<OrtApi.DGetDimensionsCount>(OrtApi.GetDimensionsCount)(info, out count),
                    "GetDimensionsCount");
                int n = count.ToInt32();
                IntPtr[] dims = new IntPtr[n];
                OrtApi.Check(api.Get<OrtApi.DGetDimensions>(OrtApi.GetDimensions)(info, dims, count),
                    "GetDimensions");
                long[] shape = new long[n];
                for (int i = 0; i < n; i++) shape[i] = dims[i].ToInt64();

                return new OrtTensor(value, shape, (OrtElementType)elementType);
            }
            finally
            {
                api.Get<OrtApi.DRelease>(OrtApi.ReleaseTensorTypeAndShapeInfo)(info);
            }
        }

        private static IntPtr[] ToPointers(long[] shape)
        {
            IntPtr[] dims = new IntPtr[shape.Length];
            for (int i = 0; i < shape.Length; i++) dims[i] = new IntPtr(shape[i]);
            return dims;
        }

        internal float[] ToFloats()
        {
            float[] result = new float[ElementCount];
            IntPtr data;
            OrtApi.Check(OrtApi.Instance.Get<OrtApi.DGetPointer>(OrtApi.GetTensorMutableData)(_value, out data),
                "GetTensorMutableData");
            Marshal.Copy(data, result, 0, result.Length);
            return result;
        }

        internal long[] ToLongs()
        {
            long[] result = new long[ElementCount];
            IntPtr data;
            OrtApi.Check(OrtApi.Instance.Get<OrtApi.DGetPointer>(OrtApi.GetTensorMutableData)(_value, out data),
                "GetTensorMutableData");
            Marshal.Copy(data, result, 0, result.Length);
            return result;
        }

        /// <summary>The shape ORT reports for this value, which is how dynamic axes become visible.</summary>
        internal long[] ActualShape()
        {
            OrtApi.Table api = OrtApi.Instance;
            IntPtr info;
            OrtApi.Check(api.Get<OrtApi.DGetTensorTypeAndShape>(OrtApi.GetTensorTypeAndShape)(_value, out info),
                "GetTensorTypeAndShape");
            try
            {
                IntPtr count;
                OrtApi.Check(api.Get<OrtApi.DGetDimensionsCount>(OrtApi.GetDimensionsCount)(info, out count),
                    "GetDimensionsCount");
                int n = count.ToInt32();
                IntPtr[] dims = new IntPtr[n];
                OrtApi.Check(api.Get<OrtApi.DGetDimensions>(OrtApi.GetDimensions)(info, dims, count),
                    "GetDimensions");
                long[] shape = new long[n];
                for (int i = 0; i < n; i++) shape[i] = dims[i].ToInt64();
                return shape;
            }
            finally
            {
                api.Get<OrtApi.DRelease>(OrtApi.ReleaseTensorTypeAndShapeInfo)(info);
            }
        }

        public void Dispose()
        {
            if (_value != IntPtr.Zero)
            {
                OrtApi.Instance.Get<OrtApi.DRelease>(OrtApi.ReleaseValue)(_value);
                _value = IntPtr.Zero;
            }
        }
    }
}
