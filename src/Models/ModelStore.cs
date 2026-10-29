using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using ScamWYF.Modding.Core;
using ScamWYF.VoiceBackend.Local;

namespace ScamWYF.VoiceBackend.Models
{
    /// <summary>
    /// Downloads the model files the local backend needs, into the plugin's own folder.
    /// </summary>
    /// <remarks>
    /// Two kinds of file arrive here. The weights are gigabytes and come from their public model
    /// repositories. The mod's own assets - the clone tuner and the pronunciation lexicon, 37 MB
    /// together - are published as assets of this mod's own release, because the catalogue installer
    /// only copies the assemblies it knows about: a release zip with a data folder beside the DLLs
    /// would install the code and none of the data, and it must work after a manual install too.
    ///
    /// Assets are pinned to the release this build came from, so a mod installed from v0.5.0 fetches
    /// v0.5.0's assets and never a newer set the DLL was not built against.
    ///
    /// Downloads are atomic: a file is written as <c>.part</c> and renamed when complete, so an
    /// interrupted download is retried rather than loaded as a truncated model. A file whose length
    /// does not match the catalog is treated as missing.
    /// </remarks>
    internal static class ModelStore
    {
        internal sealed class File
        {
            public string Name;
            public string Url;
            public long Size;
        }

        private const string Hub = "https://huggingface.co";

        private static readonly File[] ParakeetV2 =
        {
            new File
            {
                Name = "nemo128.onnx",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/nemo128.onnx", Size = 139764
            },
            new File
            {
                Name = "encoder-model.int8.onnx",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/encoder-model.int8.onnx",
                Size = 652184014
            },
            new File
            {
                Name = "decoder_joint-model.int8.onnx",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/decoder_joint-model.int8.onnx",
                Size = 8998286
            },
            new File
            {
                Name = "vocab.txt",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/vocab.txt", Size = 9384
            },
        };

        private static readonly File[] ParakeetV3 =
        {
            new File
            {
                Name = "nemo128.onnx",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/nemo128.onnx", Size = 139764
            },
            new File
            {
                Name = "encoder-model.int8.onnx",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/encoder-model.int8.onnx",
                Size = 652184014
            },
            new File
            {
                Name = "decoder_joint-model.int8.onnx",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx",
                Size = 18238178
            },
            new File
            {
                Name = "vocab.txt",
                Url = Hub + "/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt", Size = 9384
            },
        };

        private static readonly File[] Kokoro =
        {
            new File
            {
                Name = "kokoro-v1.0.onnx",
                Url = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1/kokoro-v1.0.onnx",
                Size = 325505369
            },
        };

        internal static string Root
        {
            get { return Path.Combine(Plugin.PluginDirectory, "models"); }
        }

        private static string Release
        {
            get
            {
                return "https://github.com/swyf-modding/Voice-Backend/releases/download/v" +
                       PluginBuildInfo.Version + "/";
            }
        }

        /// <summary>
        /// The mod's own runtime assets, published beside the DLL in its own release. Names are flat
        /// because a GitHub release asset cannot carry a path; they land in the folders the code
        /// expects.
        /// </summary>
        private static File[] Assets()
        {
            return new[]
            {
                new File
                {
                    Name = "inno/encoder.onnx", Url = Release + "inno-encoder.onnx", Size = 29692053
                },
                new File
                {
                    Name = "inno/tuner.bin", Url = Release + "inno-tuner.bin", Size = 5233403
                },
                new File
                {
                    Name = "inno/default-voice.bin", Url = Release + "inno-default-voice.bin", Size = 522240
                },
                new File
                {
                    Name = "g2p/en-lexicon.bin", Url = Release + "g2p-en-lexicon.bin", Size = 2555293
                },
            };
        }

        internal static long MissingAssetBytes()
        {
            File[] files = Assets();
            long total = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (!IsComplete(Path.Combine(Plugin.PluginDirectory, files[i].Name), files[i].Size))
                {
                    total += files[i].Size;
                }
            }
            return total;
        }

        /// <summary>Fetches the mod's own assets. Yields while a download runs.</summary>
        internal static IEnumerator EnsureAssets(Action<string> failed, Action done)
        {
            File[] files = Assets();
            for (int i = 0; i < files.Length; i++)
            {
                File file = files[i];
                string path = Path.Combine(Plugin.PluginDirectory, file.Name);
                if (IsComplete(path, file.Size)) continue;

                if (Plugin.Cfg != null && !Plugin.Cfg.AutoDownload.Value)
                {
                    failed("Missing " + path + " and automatic downloads are off.");
                    yield break;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                Plugin.Log.LogInfo(string.Format("Downloading {0} ({1:F0} MB) to {2}", file.Name,
                    file.Size / 1024.0 / 1024.0, directory));

                string partial = path + ".part";
                yield return Download(file.Url, partial);

                var info = new FileInfo(partial);
                if (!info.Exists || (file.Size > 0 && info.Length != file.Size))
                {
                    failed(string.Format("Download of {0} ended at {1} bytes, expected {2}. The release " +
                                         "for version {3} may not have it; see {4}",
                        file.Name, info.Exists ? info.Length : 0, file.Size, PluginBuildInfo.Version,
                        Release));
                    yield break;
                }

                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(partial, path);
            }

            if (done != null) done();
        }

        internal static File[] For(string model)
        {
            if (string.Equals(model, "kokoro", StringComparison.OrdinalIgnoreCase)) return Kokoro;
            if (string.Equals(model, "parakeet-v3", StringComparison.OrdinalIgnoreCase)) return ParakeetV3;
            return ParakeetV2;
        }

        internal static long MissingBytes(string model)
        {
            File[] files = For(model);
            string directory = Path.Combine(Root, model);
            long total = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string path = Path.Combine(directory, files[i].Name);
                if (!IsComplete(path, files[i].Size)) total += files[i].Size;
            }
            return total;
        }

        private static bool IsComplete(string path, long size)
        {
            var info = new FileInfo(path);
            return info.Exists && (size <= 0 || info.Length == size);
        }

        /// <summary>
        /// Makes sure every file for <paramref name="model"/> is on disk, downloading what is missing.
        /// Yields while a download runs; calls <paramref name="failed"/> and stops on error.
        /// </summary>
        internal static IEnumerator Ensure(string model, Action<string> progress, Action<string> failed,
            Action done)
        {
            File[] files = For(model);
            string directory = Path.Combine(Root, model);
            Directory.CreateDirectory(directory);

            for (int i = 0; i < files.Length; i++)
            {
                File file = files[i];
                string path = Path.Combine(directory, file.Name);
                if (IsComplete(path, file.Size)) continue;

                if (Plugin.Cfg != null && !Plugin.Cfg.AutoDownload.Value)
                {
                    failed("Missing " + path + " and automatic downloads are off.");
                    yield break;
                }

                Plugin.Log.LogInfo(string.Format("Downloading {0} ({1:F0} MB) to {2}", file.Name,
                    file.Size / 1024.0 / 1024.0, directory));

                string partial = path + ".part";
                yield return Download(file.Url, partial);

                var info = new FileInfo(partial);
                if (!info.Exists || (file.Size > 0 && info.Length != file.Size))
                {
                    failed(string.Format("Download of {0} ended at {1} bytes, expected {2}.", file.Name,
                        info.Exists ? info.Length : 0, file.Size));
                    yield break;
                }

                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(partial, path);
                if (progress != null) progress(file.Name);
            }

            if (done != null) done();
        }

        /// <summary>UnityWebRequest writes the body to a file, so a 600 MB model never lands in memory.</summary>
        private static IEnumerator Download(string url, string destination)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.downloadHandler = new DownloadHandlerFile(destination, false);
                request.timeout = 0;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.Log.LogError("Download failed: " + url + " - " + request.error);
                    try { System.IO.File.Delete(destination); }
                    catch (Exception) { }
                }
            }
        }
    }
}
