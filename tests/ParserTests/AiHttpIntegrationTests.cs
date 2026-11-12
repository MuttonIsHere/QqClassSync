using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QqClassSync.Services;

namespace QqSync.Tests;

/// <summary>
/// AI 解析的真实 HTTP 路径测试：用本地假服务器模拟 /chat/completions，
/// 验证请求格式、鉴权头、响应解析、失败回退，避免拿真 Key 试错。
/// </summary>
internal static class AiHttpIntegrationTests
{
    private const string OkResponse =
        "{\"choices\":[{\"message\":{\"content\":\"{\\\"Lesson\\\": 3, \\\"OriginalSubject\\\": \\\"\\\", \\\"NewSubject\\\": \\\"数学\\\"}\"}}]}";

    public static int Run()
    {
        var failed = 0;

        // 1) 成功路径
        using (var server = new MockServer(OkResponse, 200))
        {
            var settings = new TiaokeSettings
            {
                AiEnabled = true,
                AiApiKey = "sk-test-key",
                AiBaseUrl = $"http://127.0.0.1:{server.Port}/v1"
            };
            var parser = new AiTiaokeParser(settings, NullLogger<AiTiaokeParser>.Instance);
            var result = parser.TryParseAsync("今天第三节课不上了，让数学老师来上", CancellationToken.None)
                .GetAwaiter().GetResult();

            failed += Check(
                result is { Success: true }
                && result.Command!.Lesson == 3
                && result.Command.ToSubject == "数学",
                "HTTP 成功路径：自然语言 → 第3节数学");

            failed += Check(server.LastPath.Contains("/v1/chat/completions"), "请求打到 /chat/completions");
            failed += Check(server.LastBody.Contains("deepseek-chat"), "请求体带 model");
            failed += Check(server.LastBody.Contains("json_object"), "请求体带 response_format");
            failed += Check(server.LastAuth == "Bearer sk-test-key", "请求带 Bearer 鉴权头");
        }

        // 2) 服务端 500 → 静默回退（返回 null，不抛异常）
        using (var server = new MockServer("{}", 500))
        {
            var settings = new TiaokeSettings
            {
                AiEnabled = true,
                AiApiKey = "sk-test-key",
                AiBaseUrl = $"http://127.0.0.1:{server.Port}/v1"
            };
            var parser = new AiTiaokeParser(settings, NullLogger<AiTiaokeParser>.Instance);
            var result = parser.TryParseAsync("今天第三节课不上了，让数学老师来上", CancellationToken.None)
                .GetAwaiter().GetResult();
            failed += Check(result is null, "HTTP 500 → 返回 null（回退正则）");
        }

        // 3) 模型返回 {}（无调课意图）→ null
        using (var server = new MockServer(
                   "{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}", 200))
        {
            var settings = new TiaokeSettings
            {
                AiEnabled = true,
                AiApiKey = "sk-test-key",
                AiBaseUrl = $"http://127.0.0.1:{server.Port}/v1"
            };
            var parser = new AiTiaokeParser(settings, NullLogger<AiTiaokeParser>.Instance);
            var result = parser.TryParseAsync("今天作业好多", CancellationToken.None).GetAwaiter().GetResult();
            failed += Check(result is null, "模型返回 {} → null");
        }

        // 4) 未配置（空 Key）→ 不发请求
        using (var server = new MockServer(OkResponse, 200))
        {
            var settings = new TiaokeSettings
            {
                AiEnabled = true,
                AiApiKey = "",
                AiBaseUrl = $"http://127.0.0.1:{server.Port}/v1"
            };
            var parser = new AiTiaokeParser(settings, NullLogger<AiTiaokeParser>.Instance);
            var result = parser.TryParseAsync("随便说说", CancellationToken.None).GetAwaiter().GetResult();
            failed += Check(result is null, "空 Key → 直接返回 null");
            failed += Check(server.RequestCount == 0, "空 Key → 一个请求都不发");
        }

        return failed;
    }

    private static int Check(bool ok, string message)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {message}");
        return ok ? 0 : 1;
    }

    /// <summary>极简 HTTP 服务器：接受一次请求并返回固定响应。</summary>
    private sealed class MockServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _responseBody;
        private readonly int _statusCode;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serveTask;

        public int Port { get; }
        public string LastPath { get; private set; } = string.Empty;
        public string LastBody { get; private set; } = string.Empty;
        public string LastAuth { get; private set; } = string.Empty;
        public int RequestCount { get; private set; }

        public MockServer(string responseBody, int statusCode)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serveTask = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                using (client)
                using (var stream = client.GetStream())
                {
                    try
                    {
                        var request = await ReadRequestAsync(stream);
                        RequestCount++;
                        var lines = request.Headers.Split("\r\n");
                        var first = lines.FirstOrDefault() ?? string.Empty;
                        var parts = first.Split(' ');
                        if (parts.Length >= 2)
                        {
                            LastPath = parts[1];
                        }

                        LastAuth = lines
                            .FirstOrDefault(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))?
                            .Replace("Authorization:", string.Empty, StringComparison.OrdinalIgnoreCase)
                            .Trim() ?? string.Empty;
                        LastBody = request.Body;

                        var bodyBytes = Encoding.UTF8.GetBytes(_responseBody);
                        var header = $"HTTP/1.1 {_statusCode} OK\r\n" +
                                     "Content-Type: application/json\r\n" +
                                     $"Content-Length: {bodyBytes.Length}\r\n" +
                                     "Connection: close\r\n\r\n";
                        var headerBytes = Encoding.ASCII.GetBytes(header);
                        await stream.WriteAsync(headerBytes);
                        await stream.WriteAsync(bodyBytes);
                        await stream.FlushAsync();
                    }
                    catch
                    {
                        // 测试服务器不关心错误。
                    }
                }
            }
        }

        private static async Task<(string Headers, string Body)> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[16384];
            var raw = new List<byte>();
            var headerEnd = -1;
            var contentLength = 0;

            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read <= 0)
                {
                    break;
                }

                raw.AddRange(buffer.AsSpan(0, read).ToArray());
                var text = Encoding.UTF8.GetString(raw.ToArray());
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    var headers = text[..headerEnd];
                    var lengthLine = headers
                        .Split("\r\n")
                        .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    if (lengthLine is not null && int.TryParse(lengthLine.Split(':')[1].Trim(), out var len))
                    {
                        contentLength = len;
                    }

                    if (raw.Count >= headerEnd + 4 + contentLength)
                    {
                        var all = Encoding.UTF8.GetString(raw.ToArray());
                        return (all[..headerEnd], all[(headerEnd + 4)..]);
                    }
                }
            }

            var final = Encoding.UTF8.GetString(raw.ToArray());
            return (final, string.Empty);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _serveTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
