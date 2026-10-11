using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace MHToolsN.AgentServer
{
    // The wire between this server and the add-in in Navisworks. The add-in side
    // (src\MHToolsN\Bridge\BridgeWire.cs) is .NET Framework 4.8 and builds the same
    // JSON with JavaScriptSerializer; the names below are what goes over the pipe,
    // so they change on both sides together or not at all.
    //
    //   request:  { protocolVersion, sessionId, token, operationId, method, arguments }
    //   response: { ok, code, message, data }
    //
    // Frames: 4 bytes little-endian length, then UTF-8 JSON, at most 16 MiB (images).

    public sealed class BridgeEndpoint
    {
        public int protocolVersion { get; set; }
        public string sessionId { get; set; } = "";
        public string pipeName { get; set; } = "";
        public string token { get; set; } = "";
        public int processId { get; set; }
        public long processStartUtcTicks { get; set; }
        public string product { get; set; } = "";
        public string navisworksVersion { get; set; } = "";
        public string apiVersion { get; set; } = "";
        public string addinVersion { get; set; } = "";
        public string framework { get; set; } = "";
    }

    public sealed class BridgeResponse
    {
        public bool Ok;
        public string Code = "";
        public string Message = "";
        public JsonNode Data;

        public static BridgeResponse Error(string code, string message)
        {
            return new BridgeResponse { Code = code, Message = message };
        }

        public static BridgeResponse FromJson(JsonNode node)
        {
            if (node == null) return Error("INVALID_RESPONSE", "Navisworks sent an empty answer.");
            return new BridgeResponse
            {
                Ok = node["ok"] != null && node["ok"].GetValue<bool>(),
                Code = (string)node["code"] ?? "",
                Message = (string)node["message"] ?? "",
                Data = node["data"]?.DeepClone()
            };
        }
    }

    public static class Wire
    {
        public const int ProtocolVersion = 1;
        public const int MaxFrameBytes = 16 * 1024 * 1024;

        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            MaxDepth = 64,
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static string Json(object value)
        {
            return JsonSerializer.Serialize(value, Options);
        }

        public static async Task WriteAsync(Stream stream, JsonNode value, CancellationToken ct)
        {
            byte[] body = Encoding.UTF8.GetBytes(value.ToJsonString(Options));
            if (body.Length > MaxFrameBytes) throw new InvalidDataException("Frame exceeds 16 MiB.");
            byte[] header = BitConverter.GetBytes(body.Length);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            await stream.WriteAsync(header, 0, 4, ct);
            await stream.WriteAsync(body, 0, body.Length, ct);
            await stream.FlushAsync(ct);
        }

        public static async Task<JsonNode> ReadAsync(Stream stream, CancellationToken ct)
        {
            byte[] header = await ReadExactly(stream, 4, ct);
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Invalid frame length " + length + ".");
            byte[] body = await ReadExactly(stream, length, ct);
            return JsonNode.Parse(Encoding.UTF8.GetString(body), documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }

        private static async Task<byte[]> ReadExactly(Stream stream, int count, CancellationToken ct)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read, ct);
                if (n == 0) throw new EndOfStreamException("The pipe closed in the middle of a frame.");
                read += n;
            }
            return buffer;
        }
    }
}
