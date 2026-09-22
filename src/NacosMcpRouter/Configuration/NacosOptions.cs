namespace NacosMcpRouter.Configuration;

public sealed class NacosOptions
{
    public string ServerAddresses { get; init; } = "127.0.0.1:8848";
    public string Username { get; init; } = "nacos";
    public string Password { get; init; } = string.Empty;
    public string Namespace { get; init; } = string.Empty;
    public string? AccessKeyId { get; init; }
    public string? AccessKeySecret { get; init; }
}
