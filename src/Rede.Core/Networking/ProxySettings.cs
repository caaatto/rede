namespace Rede.Core.Networking;

public class ProxySettings
{
    public bool UseTor { get; set; }
    public bool UseI2P { get; set; }
    public string TorProxy { get; set; } = "socks5://127.0.0.1:9050";
    public string I2PProxy { get; set; } = "socks5://127.0.0.1:4447";
}
