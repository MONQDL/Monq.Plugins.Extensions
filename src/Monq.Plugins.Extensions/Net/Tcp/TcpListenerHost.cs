using System.Net;
using System.Net.Sockets;

namespace Monq.Plugins.Extensions.Net.Tcp;

/// <summary>
/// Accepts TCP connections and dispatches each client to an asynchronous handler.
/// </summary>
/// <remarks>
/// The host does not impose a limit on concurrent clients. It owns accepted
/// <see cref="TcpClient"/> instances and disposes each client after its handler completes.
/// </remarks>
public sealed class TcpListenerHost
{
    readonly IPEndPoint _localEndpoint;

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpListenerHost"/> class.
    /// </summary>
    /// <param name="localEndpoint">The local endpoint on which to listen.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localEndpoint"/> is <see langword="null"/>.</exception>
    public TcpListenerHost(IPEndPoint localEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);

        _localEndpoint = localEndpoint;
    }

    /// <summary>
    /// Starts listening and dispatches each accepted connection to
    /// <paramref name="handleClient"/>.
    /// </summary>
    /// <param name="handleClient">
    /// The asynchronous callback that processes an accepted client. The callback must observe
    /// <paramref name="cancellationToken"/> and handle client-specific failures.
    /// </param>
    /// <param name="onWaitingForConnection">
    /// An optional callback invoked immediately before waiting for the next connection.
    /// </param>
    /// <param name="onAcceptError">
    /// An optional callback invoked when accepting a connection fails. The listener continues
    /// accepting connections after the callback returns.
    /// </param>
    /// <param name="cancellationToken">
    /// The token used to stop accepting connections and passed to each client handler.
    /// </param>
    /// <returns>
    /// A task that completes after cancellation has stopped the listener and all accepted client
    /// handlers have completed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="handleClient"/> is <see langword="null"/>.</exception>
    /// <exception cref="SocketException">The listener could not be started.</exception>
    public async Task RunAsync(
        Func<TcpClient, CancellationToken, Task> handleClient,
        Action? onWaitingForConnection,
        Action<Exception>? onAcceptError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handleClient);

        var listener = new TcpListener(_localEndpoint);
        var clientTasks = new HashSet<Task>();
        listener.Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    onWaitingForConnection?.Invoke();
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    onAcceptError?.Invoke(exception);
                    continue;
                }

                var clientTask = HandleClient(client, handleClient, cancellationToken);
                Track(clientTasks, clientTask);
            }
        }
        finally
        {
            listener.Stop();
            await WhenAll(clientTasks).ConfigureAwait(false);
        }
    }

    static async Task HandleClient(
        TcpClient client,
        Func<TcpClient, CancellationToken, Task> handleClient,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            await handleClient(client, cancellationToken).ConfigureAwait(false);
        }
    }

    static void Track(HashSet<Task> tasks, Task task)
    {
        lock (tasks)
            tasks.Add(task);

        _ = task.ContinueWith(
            static (completedTask, state) =>
            {
                var trackedTasks = (HashSet<Task>)state!;
                lock (trackedTasks)
                    trackedTasks.Remove(completedTask);
            },
            tasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    static Task WhenAll(HashSet<Task> tasks)
    {
        lock (tasks)
            return Task.WhenAll(tasks);
    }
}
