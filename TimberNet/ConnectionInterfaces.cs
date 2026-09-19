using System;

namespace TimberNet
{
    /// <summary>
    /// Optional. A transport whose connection finishes in the background (for example a relayed Steam
    /// connection) rather than inside <see cref="ISocketStream.ConnectAsync"/>. The caller waits on a
    /// worker thread, never the game thread, because the transport may need the game thread to finish.
    /// </summary>
    public interface IConnectionAwaitable
    {
        /// <summary>Blocks until the connection is usable. Throws IOException if it failed or timed out.</summary>
        void WaitForConnection(int timeoutMilliseconds);
    }

    /// <summary>Optional. A transport that can explain why its connection ended.</summary>
    public interface IFailureDescriber
    {
        string? FailureReason { get; }
    }
}
