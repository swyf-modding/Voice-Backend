using System;
using System.Collections.Generic;
using System.Text;

namespace ScamWYF.VoiceBackend.Api
{
    /// <summary>One field of a <c>multipart/form-data</c> body.</summary>
    internal sealed class FormField
    {
        internal string Name;
        internal string FileName;
        internal string ContentType;
        internal byte[] Data;

        internal static FormField Text(string name, string value)
        {
            return new FormField
            {
                Name = name,
                Data = Encoding.UTF8.GetBytes(value ?? "")
            };
        }

        internal static FormField File(string name, string fileName, string contentType, byte[] data)
        {
            return new FormField
            {
                Name = name,
                FileName = fileName,
                ContentType = contentType ?? "application/octet-stream",
                Data = data ?? new byte[0]
            };
        }
    }

    /// <summary>
    /// Builds a multipart body.
    /// </summary>
    /// <remarks>
    /// UnityWebRequest cannot mix a JSON field with a file upload through UploadHandlerRaw alone, and
    /// two of the speech APIs want exactly that: Gladia's upload endpoint and OpenAI's transcription
    /// endpoint both take a file part plus text parts. The body is assembled here, in memory, because
    /// the largest thing that goes through it is one utterance - tens of kilobytes, not a model.
    /// </remarks>
    internal static class FormData
    {
        internal static byte[] Build(IList<FormField> fields, string boundary, out string contentType)
        {
            contentType = "multipart/form-data; boundary=" + boundary;
            List<byte> body = new List<byte>(1024);
            for (int i = 0; i < fields.Count; i++)
            {
                FormField field = fields[i];
                Append(body, "--" + boundary + "\r\n");
                Append(body, "Content-Disposition: form-data; name=\"" + field.Name + "\"");
                if (!string.IsNullOrEmpty(field.FileName))
                {
                    Append(body, "; filename=\"" + field.FileName + "\"");
                }
                Append(body, "\r\n");
                if (!string.IsNullOrEmpty(field.FileName) || !string.IsNullOrEmpty(field.ContentType))
                {
                    Append(body, "Content-Type: " + (field.ContentType ?? "application/octet-stream") + "\r\n");
                }
                Append(body, "\r\n");
                body.AddRange(field.Data ?? new byte[0]);
                Append(body, "\r\n");
            }
            Append(body, "--" + boundary + "--\r\n");
            return body.ToArray();
        }

        private static void Append(List<byte> body, string text)
        {
            body.AddRange(Encoding.UTF8.GetBytes(text));
        }

        /// <summary>A boundary that cannot collide with the payload: the hex of a fresh GUID.</summary>
        internal static string NewBoundary()
        {
            return "----VoiceBackend" + Guid.NewGuid().ToString("N");
        }
    }
}
