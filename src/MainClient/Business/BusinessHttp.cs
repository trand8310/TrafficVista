namespace TrafficVista.Business;

/// <summary>共享 HttpClient 的超时是无限；各接口在自身调用内限时并限制响应体大小。</summary>
internal static class BusinessHttp
{
    public static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int maxBytes, string name,
        CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
            throw new InvalidDataException($"{name}响应超过 {maxBytes / 1024} KB。");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new InvalidDataException($"{name}响应超过 {maxBytes / 1024} KB。");
            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }
}
