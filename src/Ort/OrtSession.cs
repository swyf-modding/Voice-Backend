using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScamWYF.VoiceBackend.Ort
{
    /// <summary>Which execution provider a session should ask for.</summary>
    internal enum OrtDevice
    {
        Cpu,
        Gpu,
    }

    /// <summary>
    /// One ONNX Runtime inference session against the copy of the runtime the game already loaded.
    /// </summary>
    /// <remarks>
    /// Sessions are expensive to build (the Parakeet encoder reads 600 MB and lays out its kernels) and
    /// are safe to run from several threads at once, so the mod keeps one per model and shares it.
    /// </remarks>
    internal sealed class OrtSession : IDisposable
    {
        private static IntPtr _env;
        private static readonly object EnvLock = new object();

        private IntPtr _session;
        private IntPtr _options;

        private OrtSession(IntPtr session, IntPtr options, string[] inputs, string[] outputs)
        {
            _session = session;
            _options = options;
            InputNames = inputs;
            OutputNames = outputs;
        }

        internal string[] InputNames { get; private set; }
        internal string[] OutputNames { get; private set; }

        internal static IntPtr CreateEnvironment()
        {
            lock (EnvLock)
            {
                if (_env != IntPtr.Zero) return _env;

                OrtApi.Table api = OrtApi.Instance;
                IntPtr id = Marshal.StringToHGlobalAnsi("ScamWYF.VoiceBackend");
                try
                {
                    IntPtr env;
                    OrtApi.Check(api.Get<OrtApi.DCreateEnv>(OrtApi.CreateEnv)(2 /* warning */, id, out env),
                        "CreateEnv");
                    _env = env;
                    return _env;
                }
                finally
                {
                    Marshal.FreeHGlobal(id);
                }
            }
        }

        internal static OrtSession Create(string modelPath, OrtDevice device, int threads, Action<string> warn)
        {
            if (!System.IO.File.Exists(modelPath))
            {
                throw new System.IO.FileNotFoundException("Model not found.", modelPath);
            }

            IntPtr env = CreateEnvironment();
            OrtApi.Table api = OrtApi.Instance;

            IntPtr options;
            OrtApi.Check(api.Get<OrtApi.DCreateSessionOptions>(OrtApi.CreateSessionOptions)(out options),
                "CreateSessionOptions");

            OrtApi.Check(api.Get<OrtApi.DSetInt>(OrtApi.SetSessionGraphOptimizationLevel)(
                options, 99 /* ORT_ENABLE_ALL */), "SetSessionGraphOptimizationLevel");

            if (threads > 0)
            {
                OrtApi.Check(api.Get<OrtApi.DSetInt>(OrtApi.SetIntraOpNumThreads)(options, threads),
                    "SetIntraOpNumThreads");
            }

            if (device == OrtDevice.Gpu && !TryAppendDml(options, warn))
            {
                warn("DirectML is not available; running " + System.IO.Path.GetFileName(modelPath) + " on the CPU.");
            }

            IntPtr path = Marshal.StringToHGlobalUni(modelPath);
            try
            {
                IntPtr session;
                IntPtr status = api.Get<OrtApi.DCreateSession>(OrtApi.CreateSession)(env, path, options, out session);
                if (status != IntPtr.Zero)
                {
                    api.Get<OrtApi.DRelease>(OrtApi.ReleaseSessionOptions)(options);
                    OrtApi.Check(status, "CreateSession(" + System.IO.Path.GetFileName(modelPath) + ")");
                }

                string[] inputs = ReadNames(session, true);
                string[] outputs = ReadNames(session, false);
                return new OrtSession(session, options, inputs, outputs);
            }
            finally
            {
                Marshal.FreeHGlobal(path);
            }
        }

        private static bool TryAppendDml(IntPtr options, Action<string> warn)
        {
            try
            {
                IntPtr module = Native.GetModuleHandle("onnxruntime.dll");
                if (module == IntPtr.Zero) return false;
                IntPtr address = Native.GetProcAddress(module, "OrtSessionOptionsAppendExecutionProvider_DML");
                if (address == IntPtr.Zero) return false;

                OrtApi.DAppendProvider append = (OrtApi.DAppendProvider)(object)Marshal.GetDelegateForFunctionPointer(
                    address, typeof(OrtApi.DAppendProvider));
                IntPtr status = append(options, 0);
                if (status != IntPtr.Zero)
                {
                    string message = Marshal.PtrToStringAnsi(OrtApi.ErrorMessage(status));
                    OrtApi.Instance.Get<OrtApi.DRelease>(OrtApi.ReleaseStatus)(status);
                    warn("DirectML provider rejected: " + message);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                warn("DirectML provider unavailable: " + ex.Message);
                return false;
            }
        }

        private static string[] ReadNames(IntPtr session, bool inputs)
        {
            OrtApi.Table api = OrtApi.Instance;
            IntPtr count;
            if (inputs)
            {
                OrtApi.Check(api.Get<OrtApi.DCountNames>(OrtApi.SessionGetInputCount)(session, out count),
                    "SessionGetInputCount");
            }
            else
            {
                OrtApi.Check(api.Get<OrtApi.DCountNames>(OrtApi.SessionGetOutputCount)(session, out count),
                    "SessionGetOutputCount");
            }

            IntPtr allocator;
            OrtApi.Check(api.Get<OrtApi.DGetAllocator>(OrtApi.GetAllocatorWithDefaultOptions)(out allocator),
                "GetAllocatorWithDefaultOptions");
            OrtApi.DAllocatorFree free = api.Get<OrtApi.DAllocatorFree>(OrtApi.AllocatorFree);

            int n = count.ToInt32();
            string[] names = new string[n];
            for (int i = 0; i < n; i++)
            {
                IntPtr name;
                if (inputs)
                {
                    OrtApi.Check(api.Get<OrtApi.DGetName>(OrtApi.SessionGetInputName)(
                        session, new IntPtr(i), allocator, out name), "SessionGetInputName");
                }
                else
                {
                    OrtApi.Check(api.Get<OrtApi.DGetName>(OrtApi.SessionGetOutputName)(
                        session, new IntPtr(i), allocator, out name), "SessionGetOutputName");
                }

                try { names[i] = Marshal.PtrToStringAnsi(name); }
                finally { free(allocator, name); }
            }
            return names;
        }

        /// <summary>
        /// Runs the session with inputs in the model's own input order and returns the outputs in the
        /// model's output order. Every returned tensor is owned by the caller.
        /// </summary>
        internal OrtTensor[] Run(OrtTensor[] inputs)
        {
            if (inputs.Length != InputNames.Length)
            {
                throw new ArgumentException(string.Format("Model wants {0} inputs, got {1}.",
                    InputNames.Length, inputs.Length));
            }

            OrtApi.Table api = OrtApi.Instance;
            IntPtr[] valueNames = ToAnsi(InputNames);
            IntPtr[] outputNames = ToAnsi(OutputNames);
            IntPtr[] inputValues = new IntPtr[inputs.Length];
            try
            {
                for (int i = 0; i < inputs.Length; i++) inputValues[i] = inputs[i].Value;

                IntPtr[] outputs = new IntPtr[OutputNames.Length];
                IntPtr status = api.Get<OrtApi.DRun>(OrtApi.Run)(_session, IntPtr.Zero, valueNames, inputValues,
                    new IntPtr(inputs.Length), outputNames, new IntPtr(OutputNames.Length), outputs);
                OrtApi.Check(status, "Run");

                OrtTensor[] result = new OrtTensor[outputs.Length];
                for (int i = 0; i < outputs.Length; i++)
                {
                    result[i] = OrtTensor.FromOutput(outputs[i]);
                }
                return result;
            }
            finally
            {
                Free(valueNames);
                Free(outputNames);
            }
        }

        private static IntPtr[] ToAnsi(string[] names)
        {
            IntPtr[] pointers = new IntPtr[names.Length];
            for (int i = 0; i < names.Length; i++) pointers[i] = Marshal.StringToHGlobalAnsi(names[i]);
            return pointers;
        }

        private static void Free(IntPtr[] pointers)
        {
            for (int i = 0; i < pointers.Length; i++)
            {
                if (pointers[i] != IntPtr.Zero) Marshal.FreeHGlobal(pointers[i]);
            }
        }

        public void Dispose()
        {
            if (_session != IntPtr.Zero)
            {
                OrtApi.Instance.Get<OrtApi.DRelease>(OrtApi.ReleaseSession)(_session);
                _session = IntPtr.Zero;
            }
            if (_options != IntPtr.Zero)
            {
                OrtApi.Instance.Get<OrtApi.DRelease>(OrtApi.ReleaseSessionOptions)(_options);
                _options = IntPtr.Zero;
            }
        }
    }
}
