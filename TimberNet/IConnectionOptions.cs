namespace TimberNet
{
    // Relay route discovery can take longer than a local TCP connection.
    public interface IConnectionOptions
    {
        int ConnectTimeoutMilliseconds { get; }
    }
    public interface IFlushableSocket
    {
        System.Threading.Tasks.Task FlushAsync();
    }
}
