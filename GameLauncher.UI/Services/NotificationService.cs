using Avalonia.Controls;
using Avalonia.Media;
using GameLauncher.UI.Services;

namespace GameLauncher.UI.Services;

public interface INotificationService
{
    event Action<Notification>? NotificationRaised;
    void Show(string title, string message, NotificationType type = NotificationType.Info);
}

public enum NotificationType
{
    Info,
    Success,
    Warning,
    Error
}

public class NotificationService : INotificationService
{
    public event Action<Notification>? NotificationRaised;

    public void Show(string title, string message, NotificationType type = NotificationType.Info)
    {
        NotificationRaised?.Invoke(new Notification(title, message, type));
    }
}

public record Notification(string Title, string Message, NotificationType Type, DateTime Timestamp = default)
{
    public DateTime Timestamp { get; } = Timestamp == default ? DateTime.Now : Timestamp;
}
