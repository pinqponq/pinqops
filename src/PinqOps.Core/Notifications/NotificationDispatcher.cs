namespace PinqOps.Notifications;

/// <summary>
/// Fans a deploy outcome out to every enabled channel, best effort: per-channel
/// timeout, failures logged and swallowed — notifications must never fail (or
/// slow down) a deploy. Plugs into <see cref="Deployer"/> as its
/// <see cref="IDeployObserver"/>.
/// </summary>
public sealed class NotificationDispatcher : IDeployObserver, IDisposable
{
    private static readonly TimeSpan ChannelTimeout = TimeSpan.FromSeconds(5);

    private readonly NotificationConfigStore _configStore;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Action<string>? _log;

    public NotificationDispatcher(string composeFilePath, Action<string>? log = null, HttpClient? httpClient = null)
    {
        _configStore = new NotificationConfigStore(composeFilePath);
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _log = log;
    }

    public async Task OnDeployCompletedAsync(DeployOutcome outcome, CancellationToken cancellationToken)
    {
        var notification = DeployNotification.FromOutcome(outcome);
        await DispatchAsync(notification, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends to all channels enabled for the notification's event.</summary>
    public async Task DispatchAsync(DeployNotification notification, CancellationToken cancellationToken = default)
    {
        var config = _configStore.Load();
        if (!config.IsEventEnabled(notification.Event))
        {
            return;
        }

        foreach (var notifier in BuildNotifiers(config))
        {
            await SendOneAsync(notifier, notification, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a synthetic test notification to ONE channel (the dashboard's
    /// "Test" button) and reports success. Unlike deploy-time dispatch, an
    /// unconfigured channel throws so the user sees why nothing arrived.
    /// </summary>
    public async Task<bool> SendTestAsync(string channel, CancellationToken cancellationToken = default)
    {
        var config = _configStore.Load();
        var notifier = BuildNotifiers(config, includeDisabled: true)
            .FirstOrDefault(n => string.Equals(n.Channel, channel, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Channel '{channel}' is not configured.");

        var notification = new DeployNotification
        {
            Event = NotificationEvents.DeploySucceeded,
            Tag = "sha-test",
            Host = Environment.MachineName,
            Timestamp = DateTimeOffset.UtcNow,
        };
        return await SendOneAsync(notifier, notification, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> SendOneAsync(
        INotifier notifier,
        DeployNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(ChannelTimeout);
            var delivered = await notifier.SendAsync(notification, timeoutSource.Token).ConfigureAwait(false);
            _log?.Invoke(delivered
                ? $"notification sent via {notifier.Channel}"
                : $"notification via {notifier.Channel} failed");
            return delivered;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke($"notification via {notifier.Channel} failed: {exception.Message}");
            return false;
        }
    }

    private IEnumerable<INotifier> BuildNotifiers(NotificationConfig config, bool includeDisabled = false)
    {
        if ((config.Webhook.Enabled || includeDisabled) && !string.IsNullOrWhiteSpace(config.Webhook.Url))
        {
            yield return new WebhookNotifier(config.Webhook.Url, _httpClient);
        }

        if ((config.Slack.Enabled || includeDisabled) && !string.IsNullOrWhiteSpace(config.Slack.WebhookUrl))
        {
            yield return new SlackNotifier(config.Slack.WebhookUrl, _httpClient);
        }

        if ((config.Telegram.Enabled || includeDisabled)
            && !string.IsNullOrWhiteSpace(config.Telegram.BotToken)
            && !string.IsNullOrWhiteSpace(config.Telegram.ChatId))
        {
            yield return new TelegramNotifier(config.Telegram.BotToken, config.Telegram.ChatId, _httpClient);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
