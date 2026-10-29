using System;
using System.Runtime.InteropServices;

namespace ScamWYF.VoiceBackend.Ort
{
    /// <summary>
    /// The slice of the ONNX Runtime C API this mod calls, resolved by ordinal out of the table
    /// <c>OrtGetApiBase()</c> hands back.
    /// </summary>
    /// <remarks>
    /// The game ships ONNX Runtime itself (<c>Scam With Your Friends_Data/Plugins/x86_64/onnxruntime.dll</c>),
    /// so nothing is redistributed here: the mod binds to the copy the process already has, in place.
    ///
    /// That DLL exports <c>OrtGetApiBase</c> and three DML/CPU helpers, and nothing else. Every other
    /// entry point lives in the <c>OrtApi</c> struct that <c>OrtGetApiBase()->GetApi(version)</c> returns,
    /// which is a flat array of function pointers laid out in declaration order. Each delegate below
    /// therefore names its ordinal, transcribed from <c>onnxruntime_c_api.h</c>. The members used here
    /// were all present by ORT 1.17, which is what the game ships (its own ABI reports 1.17.1 while the
    /// file version string claims 1.22), so the same table works on any runtime the game may bundle
    /// later: ORT accepts every API version from 1 up to its own.
    /// </remarks>
    internal static class OrtApi
    {
        private const string Dll = "onnxruntime.dll";

        // OrtApi ordinals, onnxruntime_c_api.h v1.22.1.
        internal const int GetErrorMessage = 2;
        internal const int CreateEnv = 3;
        internal const int CreateSession = 7;
        internal const int Run = 9;
        internal const int CreateSessionOptions = 10;
        internal const int SetSessionGraphOptimizationLevel = 23;
        internal const int SetIntraOpNumThreads = 24;
        internal const int SessionGetInputCount = 30;
        internal const int SessionGetOutputCount = 31;
        internal const int SessionGetInputTypeInfo = 33;
        internal const int SessionGetOutputTypeInfo = 34;
        internal const int SessionGetInputName = 36;
        internal const int SessionGetOutputName = 37;
        internal const int CreateTensorAsOrtValue = 48;
        internal const int CreateTensorWithDataAsOrtValue = 49;
        internal const int GetTensorMutableData = 51;
        internal const int CastTypeInfoToTensorInfo = 55;
        internal const int GetTensorElementType = 60;
        internal const int GetDimensionsCount = 61;
        internal const int GetDimensions = 62;
        internal const int GetTensorShapeElementCount = 64;
        internal const int GetTensorTypeAndShape = 65;
        internal const int CreateCpuMemoryInfo = 69;
        internal const int AllocatorFree = 76;
        internal const int GetAllocatorWithDefaultOptions = 78;
        internal const int ReleaseEnv = 92;
        internal const int ReleaseStatus = 93;
        internal const int ReleaseMemoryInfo = 94;
        internal const int ReleaseSession = 95;
        internal const int ReleaseValue = 96;
        internal const int ReleaseTypeInfo = 98;
        internal const int ReleaseTensorTypeAndShapeInfo = 99;
        internal const int ReleaseSessionOptions = 100;
        internal const int GetAvailableProviders = 125;
        internal const int ReleaseAvailableProviders = 126;

        /// <summary>
        /// The API version requested from GetApi. 17 is the floor this mod needs and the version the
        /// game currently ships, and ONNX Runtime serves any version in [1, its own] from one build.
        /// </summary>
        internal const int ApiVersion = 17;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetApi(uint version);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetErrorMessage(IntPtr status);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCreateEnv(int loggingLevel, IntPtr logId, out IntPtr env);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCreateSession(IntPtr env, IntPtr modelPath, IntPtr sessionOptions,
            out IntPtr session);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DRun(IntPtr session, IntPtr runOptions, IntPtr[] inputNames, IntPtr[] inputs,
            IntPtr inputCount, IntPtr[] outputNames, IntPtr outputCount, IntPtr[] outputs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCreateSessionOptions(out IntPtr options);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DSetInt(IntPtr target, int value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCountNames(IntPtr session, out IntPtr count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetName(IntPtr session, IntPtr index, IntPtr allocator, out IntPtr name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetTypeInfo(IntPtr session, IntPtr index, out IntPtr typeInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCreateTensor(IntPtr allocator, IntPtr[] shape, IntPtr shapeLength,
            int elementType, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCreateTensorWithData(IntPtr memoryInfo, IntPtr data, IntPtr dataLength,
            IntPtr[] shape, IntPtr shapeLength, int elementType, out IntPtr value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetPointer(IntPtr value, out IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCastTypeInfoToTensorInfo(IntPtr typeInfo, out IntPtr tensorInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetElementType(IntPtr tensorInfo, out int elementType);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetDimensionsCount(IntPtr tensorInfo, out IntPtr count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetDimensions(IntPtr tensorInfo, IntPtr[] dimensions, IntPtr count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetShapeElementCount(IntPtr tensorInfo, out IntPtr count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetTensorTypeAndShape(IntPtr value, out IntPtr info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DCreateCpuMemoryInfo(int allocatorType, int memoryType, out IntPtr memoryInfo);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetAllocator(out IntPtr allocator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DAllocatorFree(IntPtr allocator, IntPtr pointer);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DRelease(IntPtr target);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DAppendProvider(IntPtr options, int deviceId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate IntPtr DGetAvailableProviders(out IntPtr providers, out int count);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void DReleaseAvailableProviders(IntPtr providers, int count);

        /// <summary>Ordinal-based view of the OrtApi struct.</summary>
        internal sealed class Table
        {
            private readonly IntPtr _api;
            private readonly IntPtr[] _functions;

            internal Table(IntPtr api)
            {
                _api = api;
                // 318 members at v1.22.1; only the ones named above are ever dereferenced.
                _functions = new IntPtr[320];
                for (int i = 0; i < _functions.Length; i++)
                {
                    _functions[i] = Marshal.ReadIntPtr(api, i * IntPtr.Size);
                }
            }

            internal IntPtr Raw { get { return _api; } }

            internal T Get<T>(int ordinal) where T : class
            {
                IntPtr fn = _functions[ordinal];
                if (fn == IntPtr.Zero)
                {
                    throw new InvalidOperationException("ONNX Runtime API slot " + ordinal + " is empty.");
                }
                return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
            }
        }

        private static Table _table;
        private static DGetErrorMessage _errorMessage;

        internal static Table Instance
        {
            get
            {
                if (_table == null) _table = Load();
                return _table;
            }
        }

        internal static DGetErrorMessage ErrorMessage
        {
            get
            {
                if (_errorMessage == null) _errorMessage = Instance.Get<DGetErrorMessage>(GetErrorMessage);
                return _errorMessage;
            }
        }

        private static Table Load()
        {
            // Unity has already loaded the plugin from the game's Plugins folder by the time a mod runs,
            // so GetModuleHandle normally finds it. The LoadLibrary fallback covers the harness, where
            // the caller puts that folder on the DLL search path.
            IntPtr module = Native.GetModuleHandle(Dll);
            if (module == IntPtr.Zero) module = Native.LoadLibrary(Dll);
            if (module == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "onnxruntime.dll is not loaded and could not be loaded (Win32 error " +
                    Marshal.GetLastWin32Error() + ").");
            }

            IntPtr address = Native.GetProcAddress(module, "OrtGetApiBase");
            if (address == IntPtr.Zero)
            {
                throw new InvalidOperationException("onnxruntime.dll has no OrtGetApiBase export.");
            }

            DGetApi getApiBase = (DGetApi)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(DGetApi));
            IntPtr apiBase = getApiBase(0u);
            if (apiBase == IntPtr.Zero) throw new InvalidOperationException("OrtGetApiBase() returned null.");

            DGetApi getApi = (DGetApi)(object)Marshal.GetDelegateForFunctionPointer(
                Marshal.ReadIntPtr(apiBase, 0), typeof(DGetApi));
            IntPtr api = getApi(ApiVersion);
            if (api == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "onnxruntime.dll does not expose API version " + ApiVersion + ".");
            }

            return new Table(api);
        }

        /// <summary>Reads and clears a status pointer, turning a failure into an exception.</summary>
        internal static void Check(IntPtr status, string what)
        {
            if (status == IntPtr.Zero) return;
            string message;
            try { message = Marshal.PtrToStringAnsi(ErrorMessage(status)); }
            catch (Exception ex) { message = "<no message: " + ex.Message + ">"; }
            Instance.Get<DRelease>(ReleaseStatus)(status);
            throw new InvalidOperationException(what + " failed: " + message);
        }

        /// <summary>
        /// Proves the ordinals above are the ones this ONNX Runtime build uses. Called once before the
        /// first session; cheap, and it turns a silent crash into a message that names the problem.
        /// </summary>
        internal static string Version()
        {
            DGetAvailableProviders providers = Instance.Get<DGetAvailableProviders>(GetAvailableProviders);
            IntPtr array;
            int count;
            Check(providers(out array, out count), "GetAvailableProviders");
            string[] names = new string[count];
            for (int i = 0; i < count; i++)
            {
                names[i] = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(array, i * IntPtr.Size));
            }
            Instance.Get<DReleaseAvailableProviders>(ReleaseAvailableProviders)(array, count);
            return string.Join(",", names);
        }
    }

    internal static class Native
    {
        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr LoadLibrary(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
        internal static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
