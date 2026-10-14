namespace TrafficVista.Net.Models;

/// <summary>接口协议层错误：非成功状态码、内容无法解析、响应超限等。</summary>
public class ProxyProtocolException : Exception
{
    public ProxyProtocolException(string message) : base(message) { }

    public ProxyProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>请求超时（明确区别于调用方取消）。</summary>
public sealed class ProxyRequestTimeoutException : ProxyProtocolException
{
    public ProxyRequestTimeoutException(string message) : base(message) { }
}
