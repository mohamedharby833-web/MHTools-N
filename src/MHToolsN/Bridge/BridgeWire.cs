using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MHToolsN.Bridge
{
    /// <summary>An error the bridge reports as such: a code the agent can act on, and a message.</summary>
    internal sealed class BridgeException : Exception
    {
        public string Code { get; private set; }

        public BridgeException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>
    /// The wire between the add-in and the MCP server (src\MHToolsN.AgentServer\Wire.cs).
    /// Length-prefixed frames: 4 bytes little-endian length, then UTF-8 JSON, at most
    /// 16 MiB. Written and read with .NET Framework's own JavaScriptSerializer, so the
    /// add-in ships no JSON library that could clash with another add-in's copy.
    ///
    ///   request:  { protocolVersion, sessionId, token, operationId, method, arguments }
    ///   response: { ok, code, message, data }
    /// </summary>
    internal static class BridgeWire
    {
        public const int ProtocolVersion = 1;
        public const int MaxFrameBytes = 16 * 1024 * 1024;

        public static JavaScriptSerializer Serializer()
        {
            // JavaScriptSerializer is not thread-safe to share: one per use.
            return new JavaScriptSerializer { MaxJsonLength = MaxFrameBytes, RecursionLimit = 64 };
        }

        public static string Json(object value)
        {
            return Serializer().Serialize(value);
        }

        public static Dictionary<string, object> Parse(string json)
        {
            var value = Serializer().DeserializeObject(json) as Dictionary<string, object>;
            if (value == null) throw new InvalidDataException("The frame is not a JSON object.");
            return value;
        }

        public static Dictionary<string, object> Success(object data)
        {
            return new Dictionary<string, object> { { "ok", true }, { "code", "" }, { "message", "" }, { "data", data } };
        }

        public static Dictionary<string, object> Error(string code, string message)
        {
            return new Dictionary<string, object> { { "ok", false }, { "code", code }, { "message", message }, { "data", null } };
        }

        public static async Task WriteAsync(Stream stream, object value, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(Json(value));
            if (body.Length > MaxFrameBytes) throw new InvalidDataException("Frame exceeds 16 MiB.");
            byte[] header = BitConverter.GetBytes(body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            await stream.WriteAsync(header, 0, 4, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<Dictionary<string, object>> ReadAsync(Stream stream, CancellationToken ct)
        {
            byte[] header = await ReadExactly(stream, 4, ct).ConfigureAwait(false);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid frame length " + length + ".");
            byte[] body = await ReadExactly(stream, length, ct).ConfigureAwait(false);
            return Parse(Encoding.UTF8.GetString(body));
        }

        private static async Task<byte[]> ReadExactly(Stream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read, ct).ConfigureAwait(false);
                if (n == 0) throw new EndOfStreamException("The pipe closed in the middle of a frame.");
                read += n;
            }
            return buffer;
        }

        // ------------------------------------------------- reading arguments

        public static string Text(Dictionary<string, object> args, string name, string fallback = "")
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null) return fallback;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static int Int(Dictionary<string, object> args, string name, int fallback, int min, int max)
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null) return fallback;
            int n;
            try { n = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { throw new BridgeException("INVALID_ARGUMENT", name + " must be a whole number."); }
            return Math.Max(min, Math.Min(max, n));
        }

        public static bool Bool(Dictionary<string, object> args, string name, bool fallback)
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;
            bool parsed;
            return bool.TryParse(Convert.ToString(value), out parsed) ? parsed : fallback;
        }

        /// <summary>A list of strings: a JSON array, or one comma-separated string.</summary>
        public static List<string> Strings(Dictionary<string, object> args, string name)
        {
            var list = new List<string>();
            object value;
            if (args == null || !args.TryGetValue(name, out value) || value == null) return list;
            var text = value as string;
            if (text != null)
            {
                foreach (string part in text.Split(','))
                    if (part.Trim().Length > 0) list.Add(part.Trim());
                return list;
            }
            var items = value as System.Collections.IEnumerable;
            if (items != null)
                foreach (object item in items)
                    if (item != null && Convert.ToString(item).Trim().Length > 0) list.Add(Convert.ToString(item).Trim());
            return list;
        }

        public static Dictionary<string, object> Object(Dictionary<string, object> args, string name)
        {
            object value;
            if (args == null || !args.TryGetValue(name, out value)) return new Dictionary<string, object>();
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }
    }
}
