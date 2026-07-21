namespace PinqOps.Web;

/// <summary>
/// Resolves the port pair the compose project publishes, honoring an explicit
/// choice from the publish wizard over the automatic defaults.
/// </summary>
public static class SetupPorts
{
    /// <summary>
    /// The container-side port: the wizard's explicit value wins, then the
    /// Dockerfile's EXPOSE, then <see cref="DockerfileInspector.DefaultPort"/>.
    /// </summary>
    public static int ResolveContainer(int? requested, int? detected)
    {
        if (requested is { } port)
        {
            if (!HostPort.IsValid(port))
            {
                throw new ArgumentException($"'{port}' is not a valid container port (1-65535).");
            }

            return port;
        }

        return detected ?? DockerfileInspector.DefaultPort;
    }

    /// <summary>
    /// The host-side port. An explicit value must be free right now — a taken
    /// port would only surface later as a failed `up -d` that leaves the app
    /// stopped. Without one, the first free port from <paramref name="defaultPort"/>.
    /// </summary>
    /// <remarks>Probing is injected so the choice logic is testable without sockets.</remarks>
    public static int ResolveHost(
        int? requested, int defaultPort, Func<int, bool> isAvailable, Func<int, int?> findAvailable)
    {
        if (requested is { } port)
        {
            if (!HostPort.IsValid(port))
            {
                throw new ArgumentException($"'{port}' is not a valid host port (1-65535).");
            }

            if (!isAvailable(port))
            {
                throw new ArgumentException(
                    $"Port {port} is already in use on this server. Pick a free one — "
                    + "the deploy would fail on 'port is already allocated' and leave the app stopped.");
            }

            return port;
        }

        return findAvailable(defaultPort) ?? defaultPort;
    }
}
